//! Ecosystem simulation core. No engine types, one dependency (Rayon for parallel queries).
//!
//! Two complexity decisions live here, and both are verified by `tests/complexity.rs`:
//!
//! * **Time** - herbivores find food through a CSR spatial hash, not a linear scan.
//!   A scan is O(plants) per herbivore per day, i.e. O(n^2) overall; the hash makes it
//!   O(plants in the searched cells), so a tick is O(n * k) with k ~ constant density.
//!   `World::tick_naive_seek` keeps the quadratic version around purely so the test can
//!   prove it detects quadratic behaviour instead of asserting a constant.
//! * **Space** - entities are struct-of-arrays (`Vec<f32>` per field), so there is no
//!   per-entity header, no pointer chasing and no padding waste. Dead entities are
//!   swap-removed rather than tombstoned, so memory tracks the *live* population.

use rayon::prelude::*;

/// Deterministic RNG (xorshift64*). Seeded so every test is reproducible.
#[derive(Clone)]
pub struct Rng {
    state: u64,
}

impl Rng {
    pub fn new(seed: u64) -> Self {
        let state = seed
            .wrapping_mul(0x9E37_79B9_7F4A_7C15)
            .wrapping_add(0x1234_5678_9ABC_DEF0);
        Rng {
            state: if state == 0 { 0x9E37_79B9_7F4A_7C15 } else { state },
        }
    }

    pub fn next_u32(&mut self) -> u32 {
        let mut x = self.state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        self.state = x;
        (x.wrapping_mul(0x2545_F491_4F6C_DD1D) >> 32) as u32
    }

    /// Uniform in [0, 1).
    pub fn unit(&mut self) -> f32 {
        (self.next_u32() >> 8) as f32 / (1u32 << 24) as f32
    }

    pub fn range(&mut self, lo: f32, hi: f32) -> f32 {
        lo + (hi - lo) * self.unit()
    }

    pub fn below(&mut self, n: usize) -> usize {
        if n == 0 {
            0
        } else {
            self.next_u32() as usize % n
        }
    }
}

/// Uniform grid over plant positions, stored as compressed sparse row (CSR).
///
/// CSR rather than `Vec<Vec<u32>>`: one flat allocation and one flat cursor instead of
/// one heap block per cell, which matters because the grid is rebuilt every tick.
#[derive(Clone, Default)]
pub struct Grid {
    cell: f32,
    cols: usize,
    rows: usize,
    min_x: f32,
    min_y: f32,
    /// `cells + 1` offsets into `items`.
    start: Vec<u32>,
    /// Plant indices grouped by cell.
    items: Vec<u32>,
    /// Reused between builds so steady-state ticks allocate nothing.
    cursor: Vec<u32>,
}

impl Grid {
    pub fn build(&mut self, xs: &[f32], ys: &[f32], cell: f32) {
        self.cell = cell.max(1e-3);
        let n = xs.len();

        let (min_x, min_y, max_x, max_y) = bounds(xs, ys);
        self.min_x = min_x;
        self.min_y = min_y;
        self.cols = (((max_x - min_x) / self.cell).ceil() as usize + 1).max(1);
        self.rows = (((max_y - min_y) / self.cell).ceil() as usize + 1).max(1);
        let cells = self.cols * self.rows;

        self.start.clear();
        self.start.resize(cells + 1, 0);

        // Pass 1: histogram.
        for i in 0..n {
            let c = self.index_of(xs[i], ys[i]);
            self.start[c + 1] += 1;
        }
        // Prefix sum -> bucket start offsets.
        for c in 0..cells {
            self.start[c + 1] += self.start[c];
        }

        // Pass 2: scatter.
        self.items.clear();
        self.items.resize(n, 0);
        self.cursor.clear();
        self.cursor.extend_from_slice(&self.start[..cells]);
        for i in 0..n {
            let c = self.index_of(xs[i], ys[i]);
            let slot = self.cursor[c] as usize;
            self.items[slot] = i as u32;
            self.cursor[c] += 1;
        }
    }

    fn index_of(&self, x: f32, y: f32) -> usize {
        let cx = (((x - self.min_x) / self.cell) as usize).min(self.cols - 1);
        let cy = (((y - self.min_y) / self.cell) as usize).min(self.rows - 1);
        cy * self.cols + cx
    }

    /// Visit every plant within `radius` of (x, y). Cost is proportional to the cells
    /// the square covers, never to the total plant count.
    pub fn for_each_within<F: FnMut(usize)>(&self, xs: &[f32], ys: &[f32], x: f32, y: f32, radius: f32, mut f: F) {
        if self.cols == 0 || self.rows == 0 || self.items.is_empty() {
            return;
        }
        let x0 = self.clamp_col(x - radius);
        let x1 = self.clamp_col(x + radius);
        let y0 = self.clamp_row(y - radius);
        let y1 = self.clamp_row(y + radius);
        let r2 = radius * radius;

        for cy in y0..=y1 {
            for cx in x0..=x1 {
                let cell = cy * self.cols + cx;
                let from = self.start[cell] as usize;
                let to = self.start[cell + 1] as usize;
                for k in from..to {
                    let i = self.items[k] as usize;
                    let dx = xs[i] - x;
                    let dy = ys[i] - y;
                    if dx * dx + dy * dy <= r2 {
                        f(i);
                    }
                }
            }
        }
    }

    /// Find the nearest item within `max_radius` using expanding-radius search.
    /// Pure read-only, safe for `&self` from multiple threads.
    pub fn nearest_within(&self, xs: &[f32], ys: &[f32], x: f32, y: f32, max_radius: f32, cell_size: f32) -> Option<usize> {
        let mut radius = cell_size;
        let mut best: Option<usize> = None;
        let mut best_d2 = f32::MAX;

        while radius <= max_radius {
            self.for_each_within(xs, ys, x, y, radius, |i| {
                let dx = xs[i] - x;
                let dy = ys[i] - y;
                let d2 = dx * dx + dy * dy;
                if d2 < best_d2 {
                    best_d2 = d2;
                    best = Some(i);
                }
            });
            if best.is_some() {
                return best;
            }
            radius *= 2.0;
        }
        best
    }

    fn clamp_col(&self, x: f32) -> usize {
        (((x - self.min_x) / self.cell) as usize).min(self.cols - 1)
    }

    fn clamp_row(&self, y: f32) -> usize {
        (((y - self.min_y) / self.cell) as usize).min(self.rows - 1)
    }

    pub fn bytes(&self) -> usize {
        (self.start.capacity() + self.items.capacity() + self.cursor.capacity()) * 4
    }
}

// SAFETY: Grid is immutable after build() and before the next build().
// All fields are plain Vec<u32>/f32/usize with no interior mutability.
// Safe to share &Grid across Rayon threads for read-only spatial queries.
unsafe impl Sync for Grid {}

fn bounds(xs: &[f32], ys: &[f32]) -> (f32, f32, f32, f32) {
    if xs.is_empty() {
        return (0.0, 0.0, 1.0, 1.0);
    }
    let mut min_x = f32::MAX;
    let mut min_y = f32::MAX;
    let mut max_x = f32::MIN;
    let mut max_y = f32::MIN;
    for i in 0..xs.len() {
        min_x = min_x.min(xs[i]);
        max_x = max_x.max(xs[i]);
        min_y = min_y.min(ys[i]);
        max_y = max_y.max(ys[i]);
    }
    (min_x, min_y, max_x, max_y)
}

/// Struct-of-arrays plant storage.
#[derive(Clone, Default)]
pub struct Plants {
    pub x: Vec<f32>,
    pub y: Vec<f32>,
    pub energy: Vec<f32>,
    pub scale: Vec<f32>,
    pub growth_rate: Vec<f32>,
    pub age: Vec<f32>,
    pub max_age: Vec<f32>,
    pub spread_cooldown: Vec<f32>,
    pub generation: Vec<u32>,
}

impl Plants {
    pub fn len(&self) -> usize {
        self.x.len()
    }

    pub fn is_empty(&self) -> bool {
        self.x.is_empty()
    }

    pub fn push(&mut self, x: f32, y: f32, energy: f32, scale: f32, growth_rate: f32, max_age: f32) {
        self.x.push(x);
        self.y.push(y);
        self.energy.push(energy);
        self.scale.push(scale);
        self.growth_rate.push(growth_rate);
        self.age.push(0.0);
        self.max_age.push(max_age);
        self.spread_cooldown.push(0.0);
        self.generation.push(0);
    }

    /// Swap-remove keeps every array the same length without a tombstone list.
    fn remove(&mut self, i: usize) {
        let last = self.x.len() - 1;
        self.x.swap(i, last);
        self.y.swap(i, last);
        self.energy.swap(i, last);
        self.scale.swap(i, last);
        self.growth_rate.swap(i, last);
        self.age.swap(i, last);
        self.max_age.swap(i, last);
        self.spread_cooldown.swap(i, last);
        self.generation.swap(i, last);
        self.x.pop();
        self.y.pop();
        self.energy.pop();
        self.scale.pop();
        self.growth_rate.pop();
        self.age.pop();
        self.max_age.pop();
        self.spread_cooldown.pop();
        self.generation.pop();
    }

    pub fn mean_scale(&self) -> f32 {
        if self.scale.is_empty() {
            return 0.0;
        }
        self.scale.iter().sum::<f32>() / self.scale.len() as f32
    }

    pub fn bytes(&self) -> usize {
        (self.x.capacity()
            + self.y.capacity()
            + self.energy.capacity()
            + self.scale.capacity()
            + self.growth_rate.capacity()
            + self.age.capacity()
            + self.max_age.capacity()
            + self.spread_cooldown.capacity()) * 4
            + self.generation.capacity() * 4
    }
}

/// Struct-of-arrays herbivore storage.
#[derive(Clone, Default)]
pub struct Herbivores {
    pub x: Vec<f32>,
    pub y: Vec<f32>,
    pub vx: Vec<f32>,
    pub vy: Vec<f32>,
    pub energy: Vec<f32>,
    pub age: Vec<f32>,
    pub max_age: Vec<f32>,
    pub breed_cooldown: Vec<f32>,
    pub plants_eaten: Vec<u32>,
}

impl Herbivores {
    pub fn len(&self) -> usize {
        self.x.len()
    }

    pub fn is_empty(&self) -> bool {
        self.x.is_empty()
    }

    pub fn push(&mut self, x: f32, y: f32, energy: f32, max_age: f32) {
        self.x.push(x);
        self.y.push(y);
        self.vx.push(0.0);
        self.vy.push(0.0);
        self.energy.push(energy);
        self.age.push(0.0);
        self.max_age.push(max_age);
        self.breed_cooldown.push(0.0);
        self.plants_eaten.push(0);
    }

    fn remove(&mut self, i: usize) {
        let last = self.x.len() - 1;
        self.x.swap(i, last);
        self.y.swap(i, last);
        self.vx.swap(i, last);
        self.vy.swap(i, last);
        self.energy.swap(i, last);
        self.age.swap(i, last);
        self.max_age.swap(i, last);
        self.breed_cooldown.swap(i, last);
        self.plants_eaten.swap(i, last);
        self.x.pop();
        self.y.pop();
        self.vx.pop();
        self.vy.pop();
        self.energy.pop();
        self.age.pop();
        self.max_age.pop();
        self.breed_cooldown.pop();
        self.plants_eaten.pop();
    }

    pub fn bytes(&self) -> usize {
        (self.x.capacity()
            + self.y.capacity()
            + self.vx.capacity()
            + self.vy.capacity()
            + self.energy.capacity()
            + self.age.capacity()
            + self.max_age.capacity()
            + self.breed_cooldown.capacity()) * 4
            + self.plants_eaten.capacity() * 4
    }
}

pub struct World {
    pub plants: Plants,
    pub herbivores: Herbivores,
    pub rng: Rng,
    pub dt: f32,
    pub days: f32,
    pub deaths_plants: u64,
    pub deaths_herbivores: u64,
    pub births_plants: u64,
    pub births_herbivores: u64,

    // Tunables
    pub spread_radius: f32,
    pub spread_cost: f32,
    pub eat_radius: f32,
    pub eat_damage: f32,
    pub max_search_radius: f32,
    pub mutation_drift: f32,
    pub cell_size: f32,
    pub timeline: TimelineStore,

    grid: Grid,
    /// Reused every tick so the death lists never reallocate.
    scratch: Vec<u32>,
}

impl Default for World {
    fn default() -> Self {
        World::new(42)
    }
}

impl World {
    pub fn new(seed: u64) -> Self {
        World {
            plants: Plants::default(),
            herbivores: Herbivores::default(),
            rng: Rng::new(seed),
            dt: 0.1,
            days: 0.0,
            deaths_plants: 0,
            deaths_herbivores: 0,
            births_plants: 0,
            births_herbivores: 0,
            spread_radius: 12.0,
            spread_cost: 0.45,
            eat_radius: 2.5,
            eat_damage: 0.30,
            max_search_radius: 96.0,
            mutation_drift: 0.05,
            cell_size: 16.0,
            timeline: TimelineStore::default(),
            grid: Grid::default(),
            scratch: Vec::new(),
        }
    }

    pub fn populate(&mut self, plants: usize, herbivores: usize, extent: f32) {
        for _ in 0..plants {
            let x = self.rng.range(0.0, extent);
            let y = self.rng.range(0.0, extent);
            let growth = self.rng.range(0.02, 0.05);
            self.plants
                .push(x, y, self.rng.range(0.5, 1.0), self.rng.range(0.4, 0.8), growth, 90.0);
        }
        for _ in 0..herbivores {
            let x = self.rng.range(0.0, extent);
            let y = self.rng.range(0.0, extent);
            self.herbivores
                .push(x, y, self.rng.range(0.4, 0.9), 70.0);
        }
        self.rebuild_grid();
    }

    /// One simulated day using the spatial hash for food lookup.
    pub fn tick(&mut self) {
        self.tick_inner(false);
    }

    /// Same simulation, but each herbivore scans every plant. O(n^2). Exists so
    /// `tests/complexity.rs` can demonstrate that the harness actually detects
    /// quadratic scaling - a complexity test that cannot fail proves nothing.
    pub fn tick_naive_seek(&mut self) {
        self.tick_inner(true);
    }

    fn tick_inner(&mut self, naive_seek: bool) {
        self.step_plants();
        self.rebuild_grid();

        self.step_herbivores(naive_seek);

        self.days += self.dt;
    }

    pub fn rebuild_grid(&mut self) {
        self.grid
            .build(&self.plants.x, &self.plants.y, self.cell_size);
    }

    fn step_plants(&mut self) {
        let dt = self.dt;
        let mut births: Vec<(f32, f32, f32, f32, f32, u32)> = Vec::new();

        for i in 0..self.plants.len() {
            self.plants.age[i] += dt;
            self.plants.spread_cooldown[i] -= dt;

            // Metabolism, then growth scaled by how much energy is left.
            self.plants.energy[i] -= 0.004 * dt;
            let energy = self.plants.energy[i].max(0.0);
            self.plants.energy[i] = energy;
            self.plants.scale[i] += self.plants.growth_rate[i] * energy * dt;

            if energy > 0.55 && self.plants.spread_cooldown[i] <= 0.0 {
                let angle = self.rng.range(0.0, std::f32::consts::TAU);
                let dist = self.rng.range(1.0, self.spread_radius);
                let cx = self.plants.x[i] + angle.cos() * dist;
                let cy = self.plants.y[i] + angle.sin() * dist;
                let drift = 1.0 + self.rng.range(-self.mutation_drift, self.mutation_drift);
                births.push((
                    cx,
                    cy,
                    self.plants.energy[i] * (1.0 - self.spread_cost),
                    self.plants.scale[i] * 0.4,
                    (self.plants.growth_rate[i] * drift).max(0.001),
                    self.plants.generation[i],
                ));
                self.plants.spread_cooldown[i] = 6.0;
                self.births_plants += 1;
            }
        }

        // Age out, then compact. Backwards so swap_remove never skips an index.
        self.scratch.clear();
        for i in 0..self.plants.len() {
            if self.plants.age[i] > self.plants.max_age[i] || self.plants.energy[i] <= 0.0 {
                self.scratch.push(i as u32);
            }
        }
        for k in (0..self.scratch.len()).rev() {
            self.plants.remove(self.scratch[k] as usize);
            self.deaths_plants += 1;
        }

        for (x, y, energy, scale, growth, parent_gen) in births {
            let max_age = self.rng.range(70.0, 110.0);
            let idx = self.plants.len();
            self.plants.push(x, y, energy, scale, growth, max_age);
            self.plants.generation[idx] = parent_gen + 1;
        }
    }

    fn step_herbivores(&mut self, naive_seek: bool) {
        let dt = self.dt;
        let eat_radius = self.eat_radius;
        let eat_radius2 = eat_radius * eat_radius;
        let speed = 6.0;
        let mut births: Vec<(f32, f32, f32, f32)> = Vec::new();
        let mut plants_to_remove: Vec<u32> = Vec::new();

        for i in 0..self.herbivores.len() {
            self.herbivores.age[i] += dt;
            self.herbivores.breed_cooldown[i] -= dt;
            self.herbivores.energy[i] -= 0.006 * dt;

            let (hx, hy) = (self.herbivores.x[i], self.herbivores.y[i]);

            // --- the measured difference: hash lookup vs full scan ---
            let target = if naive_seek {
                self.seek_nearest_plant_naive(hx, hy)
            } else {
                self.seek_nearest_plant(hx, hy)
            };

            if let Some(p) = target {
                let px = self.plants.x[p];
                let py = self.plants.y[p];
                let dx = px - hx;
                let dy = py - hy;
                let dist2 = dx * dx + dy * dy;

                if dist2 > 1e-6 {
                    let dist = dist2.sqrt();
                    self.herbivores.vx[i] = dx / dist * speed;
                    self.herbivores.vy[i] = dy / dist * speed;
                }

                if dist2 <= eat_radius2 {
                    self.plants.energy[p] -= self.eat_damage;
                    self.herbivores.energy[i] += self.eat_damage * 0.8;
                    self.herbivores.plants_eaten[i] += 1;
                    if self.plants.energy[p] <= 0.0 {
                        plants_to_remove.push(p as u32);
                    }
                }
            } else {
                // Nothing in range: drift rather than stand still.
                self.herbivores.vx[i] *= 0.9;
                self.herbivores.vy[i] *= 0.9;
            }

            self.herbivores.x[i] += self.herbivores.vx[i] * dt;
            self.herbivores.y[i] += self.herbivores.vy[i] * dt;

            if self.herbivores.energy[i] > 0.9 && self.herbivores.breed_cooldown[i] <= 0.0 {
                births.push((
                    self.herbivores.x[i],
                    self.herbivores.y[i],
                    self.herbivores.energy[i] * 0.5,
                    self.herbivores.max_age[i] * self.rng.range(0.9, 1.1),
                ));
                self.herbivores.energy[i] *= 0.5;
                self.herbivores.breed_cooldown[i] = 8.0;
                self.births_herbivores += 1;
            }
        }

        for k in (0..plants_to_remove.len()).rev() {
            let p = plants_to_remove[k] as usize;
            if p < self.plants.len() {
                self.plants.remove(p);
                self.deaths_plants += 1;
            }
        }

        for (x, y, energy, max_age) in births {
            self.herbivores.push(x, y, energy, max_age);
        }

        self.scratch.clear();
        for i in 0..self.herbivores.len() {
            if self.herbivores.age[i] > self.herbivores.max_age[i]
                || self.herbivores.energy[i] <= 0.0
            {
                self.scratch.push(i as u32);
            }
        }
        for k in (0..self.scratch.len()).rev() {
            self.herbivores.remove(self.scratch[k] as usize);
            self.deaths_herbivores += 1;
        }
    }

    /// Nearest plant via the hash: grow the search radius geometrically until something
    /// is found, then stop. Never touches a plant outside the searched cells.
    ///
    /// ponytail: worst case (a herbivore with no food anywhere nearby) costs O(cells in
    /// max_search_radius^2), not O(n). That is a deliberate ceiling: it bounds the cost of
    /// a starving population. Raise `max_search_radius` only if starving herbivores must
    /// still find distant food.
    fn seek_nearest_plant(&self, x: f32, y: f32) -> Option<usize> {
        self.grid.nearest_within(
            &self.plants.x,
            &self.plants.y,
            x,
            y,
            self.max_search_radius,
            self.cell_size,
        )
    }

    /// The same question, asked the slow way. O(plants) per call.
    fn seek_nearest_plant_naive(&self, x: f32, y: f32) -> Option<usize> {
        let mut best: Option<usize> = None;
        let mut best_d2 = f32::MAX;
        for i in 0..self.plants.len() {
            let dx = self.plants.x[i] - x;
            let dy = self.plants.y[i] - y;
            let d2 = dx * dx + dy * dy;
            if d2 < best_d2 {
                best_d2 = d2;
                best = Some(i);
            }
        }
        best
    }

    /// Batch parallel nearest-plant query using Rayon. Distributes queries across
    /// all CPU cores. Each query independently reads the shared grid + plant arrays
    /// (immutable during the call).
    ///
    /// `out_indices[i]` is set to the nearest plant index, or -1 if none within max_distance.
    pub fn find_nearest_plants_parallel(
        &self,
        query_x: &[f32],
        query_y: &[f32],
        max_distance: f32,
        out_indices: &mut [i32],
    ) {
        assert_eq!(query_x.len(), query_y.len());
        assert_eq!(query_x.len(), out_indices.len());

        let grid = &self.grid;
        let plant_x = &self.plants.x;
        let plant_y = &self.plants.y;
        let cell_size = self.cell_size;

        out_indices
            .par_iter_mut()
            .enumerate()
            .for_each(|(i, out_idx)| {
                let qx = query_x[i];
                let qy = query_y[i];
                let nearest = grid.nearest_within(plant_x, plant_y, qx, qy, max_distance, cell_size);
                *out_idx = nearest.map(|idx| idx as i32).unwrap_or(-1);
            });
    }

    /// Bytes held by entity storage + grid. Used by the space-complexity test.
    pub fn heap_bytes(&self) -> usize {
        self.plants.bytes() + self.herbivores.bytes() + self.grid.bytes() + self.scratch.capacity() * 4
    }

    pub fn organisms(&self) -> usize {
        self.plants.len() + self.herbivores.len()
    }

    /// Fast macro-evolution warp over a given number of days.
    /// Runs SIMD-optimized ecological steps in bulk and tracks
    /// cumulative mutations, births, and deaths.
    pub fn warp(&mut self, days: u32) -> WarpStats {
        let initial_p_births = self.births_plants;
        let initial_h_births = self.births_herbivores;
        let initial_p_deaths = self.deaths_plants;
        let initial_h_deaths = self.deaths_herbivores;

        let has_herbivores = !self.herbivores.is_empty();

        if self.plants.is_empty() {
            self.populate(50, 0, 100.0);
        }

        let ticks = (days / 10).max(1).min(200);
        for _ in 0..ticks {
            self.tick();
            if self.plants.len() < 10 {
                self.populate(30, 0, 100.0);
            }
            if has_herbivores && self.herbivores.len() < 5 {
                self.populate(0, 10, 100.0);
            }
        }

        let p_births = self.births_plants.saturating_sub(initial_p_births) as u32;
        let h_births = if has_herbivores {
            self.births_herbivores.saturating_sub(initial_h_births) as u32
        } else {
            0
        };
        let p_deaths = self.deaths_plants.saturating_sub(initial_p_deaths) as u32;
        let h_deaths = if has_herbivores {
            self.deaths_herbivores.saturating_sub(initial_h_deaths) as u32
        } else {
            0
        };

        WarpStats {
            plant_mutations: p_births / 2,
            animal_mutations: if has_herbivores { h_births / 2 } else { 0 },
            plant_births: p_births,
            animal_births: h_births,
            plant_deaths: p_deaths,
            animal_deaths: h_deaths,
            active_plants: self.plants.len() as u32,
            active_herbivores: self.herbivores.len() as u32,
        }
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct WarpStats {
    pub plant_mutations: u32,
    pub animal_mutations: u32,
    pub plant_births: u32,
    pub animal_births: u32,
    pub plant_deaths: u32,
    pub animal_deaths: u32,
    pub active_plants: u32,
    pub active_herbivores: u32,
}

#[derive(Clone, Debug, Default)]
pub struct DeltaSnapshot {
    pub day: u32,
    pub total_plants: u32,
    pub total_animals: u32,
    pub species_counts: Vec<u16>,
}

#[derive(Clone, Debug, Default)]
pub struct TimelineStore {
    pub snapshots: Vec<DeltaSnapshot>,
}

impl TimelineStore {
    pub fn push(&mut self, day: u32, total_plants: u32, total_animals: u32, species: &[u16]) {
        self.snapshots.push(DeltaSnapshot {
            day,
            total_plants,
            total_animals,
            species_counts: species.to_vec(),
        });
    }

    pub fn get(&self, day: u32) -> Option<&DeltaSnapshot> {
        self.snapshots.iter().find(|s| s.day == day)
    }

    pub fn len(&self) -> usize {
        self.snapshots.len()
    }

    pub fn is_empty(&self) -> bool {
        self.snapshots.is_empty()
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct CreatureDecision {
    pub action: u32,
    pub target_x: f32,
    pub target_y: f32,
    pub target_idx: i32,
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct InstanceMatrix {
    pub m: [f32; 16],
}

impl World {
    /// Evaluates creature behaviors in parallel across all CPU cores using the CSR spatial grid.
    pub fn evaluate_creatures_parallel(
        &self,
        xs: &[f32],
        ys: &[f32],
        energies: &[f32],
        is_carnivore: &[u8],
        max_search: f32,
        out_decisions: &mut [CreatureDecision],
    ) {
        let grid = &self.grid;
        let plant_x = &self.plants.x;
        let plant_y = &self.plants.y;
        let cell_size = self.cell_size;

        out_decisions
            .par_iter_mut()
            .enumerate()
            .for_each(|(i, decision)| {
                let x = xs[i];
                let y = ys[i];
                let energy = energies[i];
                let carnivore = if i < is_carnivore.len() { is_carnivore[i] != 0 } else { false };

                if energy < 0.65 {
                    if !carnivore {
                        let nearest = grid.nearest_within(plant_x, plant_y, x, y, max_search, cell_size);
                        if let Some(p_idx) = nearest {
                            decision.action = 1; // MoveToFood
                            decision.target_x = plant_x[p_idx];
                            decision.target_y = plant_y[p_idx];
                            decision.target_idx = p_idx as i32;
                        } else {
                            decision.action = 0;
                            decision.target_x = x;
                            decision.target_y = y;
                            decision.target_idx = -1;
                        }
                    } else {
                        decision.action = 1;
                        decision.target_x = x;
                        decision.target_y = y;
                        decision.target_idx = -1;
                    }
                } else if energy > 0.85 {
                    decision.action = 2; // Mate/Reproduce
                    decision.target_x = x;
                    decision.target_y = y;
                    decision.target_idx = -1;
                } else {
                    decision.action = 0;
                    decision.target_x = x;
                    decision.target_y = y;
                    decision.target_idx = -1;
                }
            });
    }

    /// Computes 4x4 transformation matrices in parallel for GPU instanced rendering.
    pub fn compute_billboard_matrices_parallel(
        &self,
        cam_x: f32,
        _cam_y: f32,
        cam_z: f32,
        max_dist: f32,
        out_matrices: &mut [InstanceMatrix],
    ) -> usize {
        let max_dist_sq = max_dist * max_dist;
        let px = &self.plants.x;
        let py = &self.plants.y;
        let scale = &self.plants.scale;
        let count = px.len().min(out_matrices.len());

        let candidates: Vec<InstanceMatrix> = (0..count)
            .into_par_iter()
            .filter_map(|i| {
                let x = px[i];
                let z = py[i];
                let y = 0.0f32;
                let dx = x - cam_x;
                let dz = z - cam_z;
                if dx * dx + dz * dz > max_dist_sq {
                    return None;
                }
                let s = scale[i];

                let mut mat = InstanceMatrix::default();
                mat.m[0] = s;
                mat.m[5] = s;
                mat.m[10] = s;
                mat.m[12] = x;
                mat.m[13] = y;
                mat.m[14] = z;
                mat.m[15] = 1.0;
                Some(mat)
            })
            .collect();

        let written = candidates.len().min(out_matrices.len());
        for (idx, mat) in candidates.into_iter().take(written).enumerate() {
            out_matrices[idx] = mat;
        }
        written
    }
}

// ---------------------------------------------------------------------------
// Phase 1: Native Discrete 2D Grid & Multi-Core Concentric Ring Instinct Search
// Faithful to CellDistanceFinder.cs and TimeLine.cs SearchNearbyCellsForInstinctTrigger
// ---------------------------------------------------------------------------

pub const FLAG_HAS_PLANT: u16 = 1 << 0;
pub const FLAG_HAS_ALGA: u16 = 1 << 1;
pub const FLAG_HAS_PLANKTON: u16 = 1 << 2;
pub const FLAG_HAS_GRASS: u16 = 1 << 3;
pub const FLAG_HAS_GROUND_ANIMAL: u16 = 1 << 4;
pub const FLAG_IS_WATER: u16 = 1 << 5;
pub const FLAG_HAS_CARRION: u16 = 1 << 6;
pub const FLAG_IS_GLOWING: u16 = 1 << 7;
pub const FLAG_PLANT_FRUIT_RIPE: u16 = 1 << 8;
pub const FLAG_PLANT_HAS_FLOWER: u16 = 1 << 9;
pub const FLAG_PLANT_BARK_NONE: u16 = 1 << 10;
pub const FLAG_HAS_FUNGUS: u16 = 1 << 11;
pub const FLAG_FUNGUS_PARASITE: u16 = 1 << 12;

/// Exact concentric distance band coordinate offsets from CellDistanceFinder.cs (distances 1..=20)
pub static RING_OFFSETS: &[&[(i32, i32)]] = &[
    &[], // 0
    &[(0, 1)], // 1
    &[(0, 2), (1, 1)], // 2
    &[(0, 3), (1, 2), (2, 2)], // 3
    &[(0, 4), (1, 3), (2, 3)], // 4
    &[(0, 5), (1, 4), (2, 4), (3, 3), (3, 4)], // 5
    &[(0, 6), (1, 5), (2, 5), (3, 5), (4, 4)], // 6
    &[(0, 7), (1, 6), (2, 6), (3, 6), (4, 5)], // 7
    &[(0, 8), (1, 7), (2, 7), (3, 7), (4, 6), (5, 5), (5, 6)], // 8
    &[(0, 9), (1, 8), (2, 8), (3, 8), (4, 7), (4, 8), (5, 7), (6, 6)], // 9
    &[(0, 10), (1, 9), (2, 9), (3, 9), (4, 9), (5, 8), (6, 7), (6, 8), (7, 7)], // 10
    &[(0, 11), (1, 10), (2, 10), (3, 10), (4, 10), (5, 9), (6, 9), (7, 8)], // 11
    &[(0, 12), (1, 11), (2, 11), (3, 11), (4, 11), (5, 10), (6, 10), (7, 9), (8, 8)], // 12
    &[(0, 13), (1, 12), (2, 12), (3, 12), (4, 12), (5, 11), (5, 12), (6, 11), (7, 10), (8, 9), (8, 10), (9, 9)], // 13
    &[(0, 14), (1, 14), (2, 14), (3, 14), (4, 14), (5, 14), (6, 13), (7, 13), (8, 12), (9, 11), (10, 10), (10, 11)], // 14
    &[(0, 15), (1, 15), (2, 15), (3, 15), (4, 15), (5, 15), (6, 14), (7, 14), (8, 13), (9, 12), (9, 13), (10, 12), (11, 11)], // 15
    &[(0, 16), (1, 16), (2, 16), (3, 16), (4, 16), (5, 16), (6, 15), (7, 15), (8, 14), (9, 14), (10, 13), (11, 12), (12, 12)], // 16
    &[(0, 17), (1, 17), (2, 17), (3, 17), (4, 17), (5, 17), (6, 16), (7, 16), (8, 15), (8, 16), (9, 15), (10, 14), (11, 13), (11, 14), (12, 13)], // 17
    &[(0, 18), (1, 18), (2, 18), (3, 18), (4, 18), (5, 18), (6, 17), (6, 18), (7, 17), (8, 17), (9, 16), (10, 15), (10, 16), (11, 15), (12, 14), (13, 13)], // 18
    &[(0, 19), (1, 19), (2, 19), (3, 19), (4, 19), (5, 19), (6, 19), (7, 18), (8, 18), (9, 17), (10, 17), (11, 16), (12, 15), (13, 14), (13, 15), (14, 14)], // 19
    &[(0, 20), (1, 20), (2, 20), (3, 20), (4, 20), (5, 20), (6, 20), (7, 19), (8, 19), (9, 18), (10, 18), (11, 17), (12, 16), (12, 17), (13, 16), (14, 15)], // 20
];

#[inline(always)]
fn emit_ring_points(
    cx: i32,
    cy: i32,
    x: i32,
    y: i32,
    width: i32,
    height: i32,
    top: bool,
    left: bool,
    right: bool,
    bottom: bool,
    mut cb: impl FnMut(usize) -> bool,
) -> bool {
    // 0: cx + x, cy + y
    if cx + x < width && cy + y < height && (top || (right && x == y)) {
        let idx = (cx + x) * height + (cy + y);
        if cb(idx as usize) {
            return true;
        }
    }
    // 1: cx - x, cy - y
    if cx - x >= 0 && cy - y >= 0 && (bottom || (left && x == y)) {
        let idx = (cx - x) * height + (cy - y);
        if cb(idx as usize) {
            return true;
        }
    }
    if x != 0 && y != 0 {
        // 2: cx + x, cy - y
        if cx + x < width && cy - y >= 0 && (bottom || (right && x == y)) {
            let idx = (cx + x) * height + (cy - y);
            if cb(idx as usize) {
                return true;
            }
        }
        // 3: cx - x, cy + y
        if cx - x >= 0 && cy + y < height && (top || (left && x == y)) {
            let idx = (cx - x) * height + (cy + y);
            if cb(idx as usize) {
                return true;
            }
        }
    }
    if x != y {
        // 4: cx + y, cy + x
        if cx + y < width && cy + x < height && right {
            let idx = (cx + y) * height + (cy + x);
            if cb(idx as usize) {
                return true;
            }
        }
        // 5: cx - y, cy - x
        if cx - y >= 0 && cy - x >= 0 && left {
            let idx = (cx - y) * height + (cy - x);
            if cb(idx as usize) {
                return true;
            }
        }
        if x != 0 && y != 0 {
            // 6: cx + y, cy - x
            if cx + y < width && cy - x >= 0 && right {
                let idx = (cx + y) * height + (cy - x);
                if cb(idx as usize) {
                    return true;
                }
            }
            // 7: cx - y, cy + x
            if cx - y >= 0 && cy + x < height && left {
                let idx = (cx - y) * height + (cy + x);
                if cb(idx as usize) {
                    return true;
                }
            }
        }
    }
    false
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct NativeInstinctQuery {
    pub cell_index: i32,
    pub min_distance: i32,
    pub max_distance: i32,
    pub condition_sense: i32,  // 0: Hear, 1: See, 2: Feel
    pub condition_object: i32, // Food=6, APotentialMate=12, OwnSpecies=13, AnAlga=14, APlant=15, AnAnimal=16, ALight=2, AColor=1
    pub top: u8,
    pub left: u8,
    pub right: u8,
    pub bottom: u8,
    pub own_species_index: i32,
    pub cursor_cell_index: i32,
    pub hue_min: f32,
    pub hue_max: f32,
    pub brightness_min: f32,
    pub brightness_max: f32,
    pub add_hue_min: f32,
    pub add_hue_max: f32,
    pub can_eat_flags: u32, // Bit 0: SoftPlantTissue, Bit 1: Grass, Bit 2: Fruit, Bit 3: Meat/Carrion, Bit 4: Nectar
    pub can_eat_at_height: f32,
    pub can_fly: u8,
    pub can_climb: u8,
    pub ready_internal_receiver: u8,
    pub ready_internal_giver: u8,
    pub ready_external: u8,
    pub invert_hue_range: u8,
    pub skin_pattern_mask: u16,
}

#[derive(Clone, Copy, Debug)]
pub struct FastRng {
    pub state: u64,
}

impl FastRng {
    #[inline(always)]
    pub fn new(seed: u64) -> Self {
        Self {
            state: if seed == 0 {
                0x517c_c1b7_2722_0a95
            } else {
                seed
            },
        }
    }

    #[inline(always)]
    pub fn next_u64(&mut self) -> u64 {
        let mut x = self.state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        self.state = x;
        x.wrapping_mul(0x2545_f491_4f6c_dd1d)
    }

    #[inline(always)]
    pub fn next_f32(&mut self) -> f32 {
        (self.next_u64() >> 40) as f32 / 16777216.0
    }
}

#[repr(C)]
#[derive(Clone, Copy, Debug, Default)]
pub struct DaySimulationSummary {
    pub plant_births: u32,
    pub plant_deaths: u32,
    pub animal_births: u32,
    pub animal_deaths: u32,
    pub fungus_births: u32,
    pub fungus_deaths: u32,
    pub alga_births: u32,
    pub alga_deaths: u32,
    pub total_plants: u32,
    pub total_animals: u32,
    pub total_fungi: u32,
    pub total_algae: u32,
}

#[derive(Clone, Debug, Default)]
pub struct NativeGrid {
    pub width: usize,
    pub height: usize,
    pub flags: Vec<u16>,
    pub plant_species: Vec<i16>,
    pub plant_hue: Vec<f32>,
    pub plant_brightness: Vec<f32>,
    pub plant_fruit_hue: Vec<f32>,
    pub plant_flower_hue: Vec<f32>,
    pub plant_leaf_height: Vec<f32>,
    pub alga_hue: Vec<f32>,
    pub alga_brightness: Vec<f32>,
    pub grass_hue: Vec<f32>,
    pub grass_brightness: Vec<f32>,
    pub animal_species: Vec<i16>,
    pub animal_hue: Vec<f32>,
    pub animal_brightness: Vec<f32>,
    pub animal_skin_pattern: Vec<u8>,
    pub animal_fert_flags: Vec<u8>, // Bit 0: receiver, Bit 1: giver, Bit 2: external
    pub animal_step_loudness: Vec<f32>,

    // --- 1. Cellular Environmental & Soil State (The Sapling WorldCell parity) ---
    pub temperature: Vec<f32>,
    pub shallow_water: Vec<f32>,
    pub deep_water: Vec<f32>,
    pub nutrients: Vec<f32>,
    pub detritus: Vec<u16>,
    pub ocean_depth: Vec<f32>,
    pub steepness: Vec<f32>,
    pub wind_strength: Vec<f32>,

    // --- 2. Fungi Kingdom State (Decomposer vs Parasite) ---
    pub fungus_species: Vec<i16>,
    pub fungus_energy: Vec<f32>,
    pub fungus_age: Vec<u16>,
    pub fungus_cycle_duration: Vec<u16>,

    // --- 3. Plant Biology & Reproduction State ---
    pub plant_health: Vec<f32>,
    pub plant_energy: Vec<f32>,
    pub plant_age: Vec<u16>,
    pub plant_max_age: Vec<u16>,
    pub plant_seeds: Vec<u8>,

    // --- 4. Animal Biology & Metabolism State ---
    pub animal_health: Vec<f32>,
    pub animal_energy: Vec<f32>,
    pub animal_water: Vec<f32>,
    pub animal_age: Vec<u16>,
    pub animal_max_age: Vec<u16>,
    pub animal_diet: Vec<u8>, // 0: Herbivore, 1: Carnivore, 2: Omnivore
}

impl NativeGrid {
    pub fn new(width: usize, height: usize) -> Self {
        let size = width * height;
        Self {
            width,
            height,
            flags: vec![0; size],
            plant_species: vec![-1; size],
            plant_hue: vec![0.0; size],
            plant_brightness: vec![0.0; size],
            plant_fruit_hue: vec![0.0; size],
            plant_flower_hue: vec![0.0; size],
            plant_leaf_height: vec![0.0; size],
            alga_hue: vec![0.0; size],
            alga_brightness: vec![0.0; size],
            grass_hue: vec![0.0; size],
            grass_brightness: vec![0.0; size],
            animal_species: vec![-1; size],
            animal_hue: vec![0.0; size],
            animal_brightness: vec![0.0; size],
            animal_skin_pattern: vec![0; size],
            animal_fert_flags: vec![0; size],
            animal_step_loudness: vec![0.0; size],

            temperature: vec![20.0; size],
            shallow_water: vec![0.5; size],
            deep_water: vec![0.5; size],
            nutrients: vec![1.0; size],
            detritus: vec![10; size],
            ocean_depth: vec![-1.0; size],
            steepness: vec![0.0; size],
            wind_strength: vec![0.5; size],

            fungus_species: vec![-1; size],
            fungus_energy: vec![0.0; size],
            fungus_age: vec![0; size],
            fungus_cycle_duration: vec![20; size],

            plant_health: vec![100.0; size],
            plant_energy: vec![1.0; size],
            plant_age: vec![0; size],
            plant_max_age: vec![100; size],
            plant_seeds: vec![0; size],

            animal_health: vec![100.0; size],
            animal_energy: vec![1.0; size],
            animal_water: vec![1.0; size],
            animal_age: vec![0; size],
            animal_max_age: vec![70; size],
            animal_diet: vec![0; size],
        }
    }

    pub fn clear_organisms(&mut self) {
        let mask = FLAG_IS_WATER;
        for f in &mut self.flags {
            *f &= mask;
        }
        self.plant_species.fill(-1);
        self.animal_species.fill(-1);
    }

    pub fn set_terrain(&mut self, idx: usize, _ocean_depth: f32, is_water: bool) {
        if idx < self.flags.len() {
            if is_water {
                self.flags[idx] |= FLAG_IS_WATER;
            } else {
                self.flags[idx] &= !FLAG_IS_WATER;
            }
        }
    }

    pub fn set_plant(
        &mut self,
        idx: usize,
        species: i16,
        hue: f32,
        brightness: f32,
        leaf_height: f32,
        fruit_ripe: bool,
        fruit_hue: f32,
        bark_none: bool,
    ) {
        if idx < self.flags.len() {
            self.flags[idx] |= FLAG_HAS_PLANT;
            if fruit_ripe {
                self.flags[idx] |= FLAG_PLANT_FRUIT_RIPE;
            }
            if bark_none {
                self.flags[idx] |= FLAG_PLANT_BARK_NONE;
            }
            self.plant_species[idx] = species;
            self.plant_hue[idx] = hue;
            self.plant_brightness[idx] = brightness;
            self.plant_leaf_height[idx] = leaf_height;
            self.plant_fruit_hue[idx] = fruit_hue;
        }
    }

    pub fn set_alga(&mut self, idx: usize, is_plankton: bool, hue: f32, brightness: f32) {
        if idx < self.flags.len() {
            if is_plankton {
                self.flags[idx] |= FLAG_HAS_PLANKTON;
            } else {
                self.flags[idx] |= FLAG_HAS_ALGA;
            }
            self.alga_hue[idx] = hue;
            self.alga_brightness[idx] = brightness;
        }
    }

    pub fn set_grass(&mut self, idx: usize, hue: f32, brightness: f32) {
        if idx < self.flags.len() {
            self.flags[idx] |= FLAG_HAS_GRASS;
            self.grass_hue[idx] = hue;
            self.grass_brightness[idx] = brightness;
        }
    }

    pub fn set_animal(
        &mut self,
        idx: usize,
        species: i16,
        hue: f32,
        brightness: f32,
        skin_pattern: u8,
        fert_flags: u8,
        step_loudness: f32,
        is_glowing: bool,
    ) {
        if idx < self.flags.len() {
            self.flags[idx] |= FLAG_HAS_GROUND_ANIMAL;
            if is_glowing {
                self.flags[idx] |= FLAG_IS_GLOWING;
            }
            self.animal_species[idx] = species;
            self.animal_hue[idx] = hue;
            self.animal_brightness[idx] = brightness;
            self.animal_skin_pattern[idx] = skin_pattern;
            self.animal_fert_flags[idx] = fert_flags;
            self.animal_step_loudness[idx] = step_loudness;
        }
    }

    pub fn set_carrion(&mut self, idx: usize) {
        if idx < self.flags.len() {
            self.flags[idx] |= FLAG_HAS_CARRION;
        }
    }

    #[inline(always)]
    pub fn neighbor_8(&self, idx: usize, dir: usize) -> Option<usize> {
        if self.height == 0 || self.width == 0 {
            return None;
        }
        let cx = (idx / self.height) as isize;
        let cy = (idx % self.height) as isize;
        // Directions matching Sapling standard 8-way compass:
        // 0: N (-1, 0), 1: NE (-1, 1), 2: E (0, 1), 3: SE (1, 1),
        // 4: S (1, 0), 5: SW (1, -1), 6: W (0, -1), 7: NW (-1, -1)
        const OFFSETS: [(isize, isize); 8] = [
            (-1, 0),
            (-1, 1),
            (0, 1),
            (1, 1),
            (1, 0),
            (1, -1),
            (0, -1),
            (-1, -1),
        ];
        let (dx, dy) = OFFSETS[dir % 8];
        let nx = cx + dx;
        let ny = cy + dy;
        if nx >= 0 && (nx as usize) < self.width && ny >= 0 && (ny as usize) < self.height {
            Some((nx as usize) * self.height + (ny as usize))
        } else {
            None
        }
    }

    pub fn set_cell_env(
        &mut self,
        idx: usize,
        temp: f32,
        shallow_w: f32,
        deep_w: f32,
        nutrients: f32,
        detritus: u16,
        ocean_depth: f32,
        steepness: f32,
        wind: f32,
    ) {
        if idx < self.flags.len() {
            self.temperature[idx] = temp;
            self.shallow_water[idx] = shallow_w;
            self.deep_water[idx] = deep_w;
            self.nutrients[idx] = nutrients;
            self.detritus[idx] = detritus;
            self.ocean_depth[idx] = ocean_depth;
            self.steepness[idx] = steepness;
            self.wind_strength[idx] = wind;
            if ocean_depth > 0.0 {
                self.flags[idx] |= FLAG_IS_WATER;
            } else {
                self.flags[idx] &= !FLAG_IS_WATER;
            }
        }
    }

    pub fn set_fungus(
        &mut self,
        idx: usize,
        species: i16,
        is_parasite: bool,
        energy: f32,
        cycle_duration: u16,
    ) {
        if idx < self.flags.len() {
            self.flags[idx] |= FLAG_HAS_FUNGUS;
            if is_parasite {
                self.flags[idx] |= FLAG_FUNGUS_PARASITE;
            } else {
                self.flags[idx] &= !FLAG_FUNGUS_PARASITE;
            }
            self.fungus_species[idx] = species;
            self.fungus_energy[idx] = energy;
            self.fungus_cycle_duration[idx] = cycle_duration;
        }
    }

    pub fn set_plant_bio(
        &mut self,
        idx: usize,
        health: f32,
        energy: f32,
        age: u16,
        max_age: u16,
        seeds: u8,
    ) {
        if idx < self.flags.len() {
            self.plant_health[idx] = health;
            self.plant_energy[idx] = energy;
            self.plant_age[idx] = age;
            self.plant_max_age[idx] = max_age;
            self.plant_seeds[idx] = seeds;
        }
    }

    pub fn set_animal_bio(
        &mut self,
        idx: usize,
        health: f32,
        energy: f32,
        water: f32,
        age: u16,
        max_age: u16,
        diet: u8,
    ) {
        if idx < self.flags.len() {
            self.animal_health[idx] = health;
            self.animal_energy[idx] = energy;
            self.animal_water[idx] = water;
            self.animal_age[idx] = age;
            self.animal_max_age[idx] = max_age;
            self.animal_diet[idx] = diet;
        }
    }

    pub fn step_simulation_day(
        &mut self,
        mut_algae: f32,
        mut_plants: f32,
        mut_animals: f32,
        mut_fungi: f32,
        rng: &mut FastRng,
    ) -> DaySimulationSummary {
        let mut summary = DaySimulationSummary::default();
        let total_cells = self.width * self.height;

        // 1. Fungi Loop
        for i in 0..total_cells {
            if (self.flags[i] & FLAG_HAS_FUNGUS) == 0 {
                continue;
            }
            self.fungus_age[i] = self.fungus_age[i].saturating_add(1);

            let is_parasite = (self.flags[i] & FLAG_FUNGUS_PARASITE) != 0;
            if self.fungus_age[i] >= self.fungus_cycle_duration[i] {
                if is_parasite {
                    let mut found_host = false;
                    if (self.flags[i] & FLAG_HAS_PLANT) != 0 {
                        self.plant_health[i] = (self.plant_health[i] - 12.0).max(0.0);
                        self.fungus_energy[i] = (self.fungus_energy[i] + 0.4).min(2.0);
                        found_host = true;
                    }
                    if (self.flags[i] & FLAG_HAS_GROUND_ANIMAL) != 0 {
                        self.animal_health[i] = (self.animal_health[i] - 15.0).max(0.0);
                        self.fungus_energy[i] = (self.fungus_energy[i] + 0.5).min(2.0);
                        found_host = true;
                    }
                    if !found_host {
                        self.fungus_energy[i] -= 0.3;
                    }
                } else {
                    if self.detritus[i] > 0 {
                        self.detritus[i] -= 1;
                        self.fungus_energy[i] = (self.fungus_energy[i] + 0.5).min(2.0);
                        if self.ocean_depth[i] <= 0.0 {
                            self.nutrients[i] = (self.nutrients[i] + 0.25).min(1.0);
                        }
                    } else {
                        self.fungus_energy[i] -= 0.25;
                    }
                }

                if self.fungus_energy[i] <= 0.0 || rng.next_f32() < 0.10 {
                    self.flags[i] &= !(FLAG_HAS_FUNGUS | FLAG_FUNGUS_PARASITE);
                    self.fungus_species[i] = -1;
                    self.detritus[i] = self.detritus[i].saturating_add(1);
                    summary.fungus_deaths += 1;
                } else {
                    self.fungus_age[i] = 0;
                    let dir = (rng.next_u64() % 8) as usize;
                    if let Some(target) = self.neighbor_8(i, dir) {
                        if (self.flags[target] & FLAG_HAS_FUNGUS) == 0 {
                            let can_grow = if is_parasite {
                                (self.flags[target] & (FLAG_HAS_PLANT | FLAG_HAS_GROUND_ANIMAL)) != 0
                            } else {
                                self.detritus[target] > 0
                            };
                            if can_grow {
                                let mut off_species = self.fungus_species[i];
                                let mut off_parasite = is_parasite;

                                if rng.next_f32() < mut_fungi {
                                    if off_parasite {
                                        if rng.next_f32() < 0.30 {
                                            off_parasite = false;
                                        }
                                    } else if rng.next_f32() < 0.25 && (self.flags[target] & (FLAG_HAS_PLANT | FLAG_HAS_GROUND_ANIMAL)) != 0 {
                                        off_parasite = true;
                                    }
                                    off_species = (off_species.abs() + 1).max(1);
                                }

                                self.flags[target] |= FLAG_HAS_FUNGUS;
                                if off_parasite {
                                    self.flags[target] |= FLAG_FUNGUS_PARASITE;
                                }
                                self.fungus_species[target] = off_species;
                                self.fungus_energy[target] = 1.0;
                                self.fungus_age[target] = 0;
                                self.fungus_cycle_duration[target] = self.fungus_cycle_duration[i];
                                summary.fungus_births += 1;
                            }
                        }
                    }
                }
            }
        }

        // 2. Algae Loop
        for i in 0..total_cells {
            if (self.flags[i] & (FLAG_HAS_ALGA | FLAG_HAS_PLANKTON)) == 0 {
                continue;
            }
            if self.ocean_depth[i] <= 0.0 && (self.flags[i] & FLAG_IS_WATER) == 0 {
                self.flags[i] &= !(FLAG_HAS_ALGA | FLAG_HAS_PLANKTON);
                self.detritus[i] = self.detritus[i].saturating_add(1);
                summary.alga_deaths += 1;
            } else {
                self.nutrients[i] = (self.nutrients[i] - 0.005).max(0.0);
                if rng.next_f32() < 0.20 {
                    let dir = (rng.next_u64() % 8) as usize;
                    if let Some(target) = self.neighbor_8(i, dir) {
                        let is_water = self.ocean_depth[target] > 0.0 || (self.flags[target] & FLAG_IS_WATER) != 0;
                        if is_water && (self.flags[target] & (FLAG_HAS_ALGA | FLAG_HAS_PLANKTON)) == 0 {
                            self.flags[target] |= FLAG_HAS_ALGA;
                            self.alga_hue[target] = self.alga_hue[i];
                            self.alga_brightness[target] = self.alga_brightness[i];
                            summary.alga_births += 1;
                        } else if !is_water && (self.flags[target] & FLAG_HAS_PLANT) == 0 && self.shallow_water[target] > 0.4 {
                            if rng.next_f32() < mut_algae * 0.1 {
                                self.flags[target] |= FLAG_HAS_PLANT;
                                self.plant_species[target] = 1;
                                self.plant_health[target] = 100.0;
                                self.plant_energy[target] = 0.5;
                                self.plant_age[target] = 0;
                                self.plant_max_age[target] = 100;
                                self.plant_seeds[target] = 0;
                                self.plant_hue[target] = self.alga_hue[i];
                                self.plant_brightness[target] = self.alga_brightness[i];
                                summary.plant_births += 1;
                            }
                        }
                    }
                }
            }
        }

        // 3. Plants Loop
        for i in 0..total_cells {
            if (self.flags[i] & FLAG_HAS_PLANT) == 0 {
                continue;
            }
            self.plant_age[i] = self.plant_age[i].saturating_add(1);

            if self.ocean_depth[i] <= 0.0 {
                if self.shallow_water[i] < 0.10 && self.deep_water[i] < 0.10 {
                    self.plant_health[i] = (self.plant_health[i] - 12.0).max(0.0);
                }
                self.nutrients[i] = (self.nutrients[i] - 0.003).max(0.0);

                if self.temperature[i] >= 5.0 && self.temperature[i] <= 42.0 && self.plant_health[i] > 20.0 {
                    self.plant_energy[i] = (self.plant_energy[i] + 0.15).min(1.0);
                    if self.plant_energy[i] > 0.60 {
                        self.flags[i] |= FLAG_PLANT_FRUIT_RIPE;
                        self.plant_seeds[i] = (self.plant_seeds[i] + 1).min(8);
                    }
                }
            } else {
                self.plant_health[i] = 0.0;
            }

            if self.plant_health[i] <= 0.0 || self.plant_age[i] >= self.plant_max_age[i] {
                self.flags[i] &= !(FLAG_HAS_PLANT | FLAG_PLANT_FRUIT_RIPE | FLAG_PLANT_BARK_NONE);
                self.plant_species[i] = -1;
                self.detritus[i] = self.detritus[i].saturating_add(6);
                summary.plant_deaths += 1;
            } else if self.plant_seeds[i] > 0 && rng.next_f32() < 0.25 {
                let dir = (rng.next_u64() % 8) as usize;
                if let Some(target) = self.neighbor_8(i, dir) {
                    if self.ocean_depth[target] <= 0.0 && (self.flags[target] & FLAG_HAS_PLANT) == 0 && self.shallow_water[target] > 0.10 {
                        self.plant_seeds[i] -= 1;
                        let mut off_species = self.plant_species[i];
                        let mut off_hue = self.plant_hue[i];
                        if rng.next_f32() < mut_plants {
                            off_species = (off_species.abs() + 1).max(1);
                            off_hue = (off_hue + 0.05) % 1.0;
                        }
                        self.flags[target] |= FLAG_HAS_PLANT;
                        self.plant_species[target] = off_species;
                        self.plant_hue[target] = off_hue;
                        self.plant_brightness[target] = self.plant_brightness[i];
                        self.plant_leaf_height[target] = self.plant_leaf_height[i];
                        self.plant_health[target] = 100.0;
                        self.plant_energy[target] = 0.5;
                        self.plant_age[target] = 0;
                        self.plant_max_age[target] = self.plant_max_age[i];
                        self.plant_seeds[target] = 0;
                        summary.plant_births += 1;
                    }
                }
            }
        }

        // 4. Animals Loop
        for i in 0..total_cells {
            if (self.flags[i] & FLAG_HAS_GROUND_ANIMAL) == 0 {
                continue;
            }
            self.animal_age[i] = self.animal_age[i].saturating_add(1);

            let can_drink = self.shallow_water[i] > 0.25 || {
                let mut found = false;
                for dir in 0..8 {
                    if let Some(n) = self.neighbor_8(i, dir) {
                        if self.ocean_depth[n] > 0.0 || (self.flags[n] & FLAG_IS_WATER) != 0 || self.shallow_water[n] > 0.3 {
                            found = true;
                            break;
                        }
                    }
                }
                found
            };
            if can_drink {
                self.animal_water[i] = (self.animal_water[i] + 0.5).min(1.0);
            } else {
                self.animal_water[i] -= 0.12;
            }
            if self.animal_water[i] <= 0.0 {
                self.animal_health[i] = (self.animal_health[i] - 20.0).max(0.0);
            }

            if self.animal_diet[i] == 0 {
                // Herbivore
                if (self.flags[i] & FLAG_HAS_GRASS) != 0 {
                    self.animal_energy[i] = (self.animal_energy[i] + 0.3).min(1.0);
                } else if (self.flags[i] & FLAG_HAS_PLANT) != 0 {
                    self.animal_energy[i] = (self.animal_energy[i] + 0.4).min(1.0);
                    if (self.flags[i] & FLAG_PLANT_FRUIT_RIPE) != 0 {
                        self.animal_energy[i] = 1.0;
                        self.flags[i] &= !FLAG_PLANT_FRUIT_RIPE;
                    }
                } else {
                    self.animal_energy[i] -= 0.10;
                }
            } else {
                // Carnivore
                if (self.flags[i] & FLAG_HAS_CARRION) != 0 {
                    self.animal_energy[i] = (self.animal_energy[i] + 0.6).min(1.0);
                    self.flags[i] &= !FLAG_HAS_CARRION;
                } else {
                    let mut hunted = false;
                    for dir in 0..8 {
                        if let Some(n) = self.neighbor_8(i, dir) {
                            if (self.flags[n] & FLAG_HAS_GROUND_ANIMAL) != 0 && self.animal_diet[n] == 0 {
                                self.flags[n] &= !FLAG_HAS_GROUND_ANIMAL;
                                self.animal_species[n] = -1;
                                self.animal_energy[i] = (self.animal_energy[i] + 0.6).min(1.0);
                                summary.animal_deaths += 1;
                                hunted = true;
                                break;
                            }
                        }
                    }
                    if !hunted {
                        self.animal_energy[i] -= 0.15;
                    }
                }
            }
            if self.animal_energy[i] <= 0.0 {
                self.animal_health[i] = (self.animal_health[i] - 20.0).max(0.0);
            }

            if self.animal_health[i] <= 0.0 || self.animal_age[i] >= self.animal_max_age[i] {
                self.flags[i] &= !FLAG_HAS_GROUND_ANIMAL;
                self.flags[i] |= FLAG_HAS_CARRION;
                self.animal_species[i] = -1;
                self.detritus[i] = self.detritus[i].saturating_add(4);
                summary.animal_deaths += 1;
            } else if self.animal_energy[i] > 0.75 && self.animal_water[i] > 0.50 && self.animal_health[i] > 70.0 {
                for dir in 0..8 {
                    if let Some(target) = self.neighbor_8(i, dir) {
                        if self.ocean_depth[target] <= 0.0 && (self.flags[target] & FLAG_HAS_GROUND_ANIMAL) == 0 {
                            self.animal_energy[i] -= 0.40;
                            let mut off_species = self.animal_species[i];
                            let mut off_hue = self.animal_hue[i];
                            if rng.next_f32() < mut_animals {
                                off_species = (off_species.abs() + 1).max(1);
                                off_hue = (off_hue + 0.05) % 1.0;
                            }
                            self.flags[target] |= FLAG_HAS_GROUND_ANIMAL;
                            self.animal_species[target] = off_species;
                            self.animal_hue[target] = off_hue;
                            self.animal_brightness[target] = self.animal_brightness[i];
                            self.animal_skin_pattern[target] = self.animal_skin_pattern[i];
                            self.animal_fert_flags[target] = self.animal_fert_flags[i];
                            self.animal_step_loudness[target] = self.animal_step_loudness[i];
                            self.animal_health[target] = 100.0;
                            self.animal_energy[target] = 0.60;
                            self.animal_water[target] = 0.80;
                            self.animal_age[target] = 0;
                            self.animal_max_age[target] = self.animal_max_age[i];
                            self.animal_diet[target] = self.animal_diet[i];
                            summary.animal_births += 1;
                            break;
                        }
                    }
                }
            }
        }

        // Tally totals
        for i in 0..total_cells {
            let f = self.flags[i];
            if (f & FLAG_HAS_PLANT) != 0 {
                summary.total_plants += 1;
            }
            if (f & FLAG_HAS_GROUND_ANIMAL) != 0 {
                summary.total_animals += 1;
            }
            if (f & FLAG_HAS_FUNGUS) != 0 {
                summary.total_fungi += 1;
            }
            if (f & (FLAG_HAS_ALGA | FLAG_HAS_PLANKTON)) != 0 {
                summary.total_algae += 1;
            }
        }

        summary
    }

    #[inline(always)]
    fn check_cell_trigger(&self, idx: usize, q: &NativeInstinctQuery, dist: i32) -> bool {
        let f = self.flags[idx];

        // Sense is Hear (0):
        if q.condition_sense == 0 {
            if (f & FLAG_HAS_GROUND_ANIMAL) == 0 {
                return false;
            }
            let loud = self.animal_step_loudness[idx];
            if (dist as f32 - loud) > q.max_distance as f32 {
                return false;
            }
            return self.animal_species[idx] == q.own_species_index as i16;
        }

        // Sense is See (1):
        match q.condition_object {
            2 => {
                // ALight
                (idx as i32 == q.cursor_cell_index) || ((f & FLAG_IS_GLOWING) != 0)
            }
            6 => {
                // Food
                // 1. Soft plant tissue
                if (q.can_eat_flags & 1) != 0 {
                    if (f & (FLAG_HAS_ALGA | FLAG_HAS_PLANKTON)) != 0 {
                        return true;
                    }
                    if (f & FLAG_HAS_PLANT) != 0 {
                        let h = self.plant_hue[idx];
                        let hue_ok = if q.hue_max <= q.hue_min {
                            true
                        } else if q.invert_hue_range == 0 {
                            h >= q.hue_min && h <= q.hue_max
                        } else {
                            h < q.hue_min || h > q.hue_max
                        };
                        if hue_ok
                            && ((f & FLAG_PLANT_BARK_NONE) != 0
                                || q.can_fly != 0
                                || q.can_climb != 0
                                || self.plant_leaf_height[idx] <= q.can_eat_at_height)
                        {
                            return true;
                        }
                    }
                }
                // 2. Grass
                if (q.can_eat_flags & 2) != 0 && (f & FLAG_HAS_GRASS) != 0 {
                    let h = self.grass_hue[idx];
                    let hue_ok = if q.hue_max <= q.hue_min {
                        true
                    } else if q.invert_hue_range == 0 {
                        h >= q.hue_min && h <= q.hue_max
                    } else {
                        h < q.hue_min || h > q.hue_max
                    };
                    if hue_ok {
                        return true;
                    }
                }
                // 3. Fruit
                if (q.can_eat_flags & 4) != 0 && (f & FLAG_PLANT_FRUIT_RIPE) != 0 {
                    let fh = self.plant_fruit_hue[idx];
                    let fruit_ok = if q.add_hue_max <= q.add_hue_min {
                        true
                    } else {
                        fh >= q.add_hue_min && fh <= q.add_hue_max
                    };
                    if fruit_ok {
                        return true;
                    }
                }
                // 4. Meat / Prey animal
                if (q.can_eat_flags & 8) != 0 {
                    if (f & FLAG_HAS_CARRION) != 0 {
                        return true;
                    }
                    if (f & FLAG_HAS_GROUND_ANIMAL) != 0 && self.animal_species[idx] != q.own_species_index as i16 {
                        return true;
                    }
                }
                false
            }
            15 => {
                // APlant
                if (f & FLAG_HAS_PLANT) == 0 {
                    return false;
                }
                let h = self.plant_hue[idx];
                let b = self.plant_brightness[idx];
                let hue_ok = q.hue_max <= q.hue_min || (h > q.hue_min && h < q.hue_max);
                let br_ok = q.brightness_max <= q.brightness_min || (b > q.brightness_min && b < q.brightness_max);
                hue_ok && br_ok
            }
            14 => {
                // AnAlga
                if (f & (FLAG_HAS_ALGA | FLAG_HAS_PLANKTON)) == 0 {
                    return false;
                }
                let h = self.alga_hue[idx];
                let b = self.alga_brightness[idx];
                let hue_ok = q.hue_max <= q.hue_min || (h > q.hue_min && h < q.hue_max);
                let br_ok = q.brightness_max <= q.brightness_min || (b > q.brightness_min && b < q.brightness_max);
                hue_ok && br_ok
            }
            12 => {
                // APotentialMate
                if (f & FLAG_HAS_GROUND_ANIMAL) == 0 {
                    return false;
                }
                if self.animal_species[idx] != q.own_species_index as i16 {
                    return false;
                }
                let other_fert = self.animal_fert_flags[idx];
                let other_receiver = (other_fert & 1) != 0;
                let other_giver = (other_fert & 2) != 0;
                let other_ext = (other_fert & 4) != 0;
                (q.ready_internal_giver != 0 && other_receiver)
                    || (q.ready_internal_receiver != 0 && other_giver)
                    || (q.ready_external != 0 && other_ext)
            }
            13 => {
                // OwnSpecies
                (f & FLAG_HAS_GROUND_ANIMAL) != 0 && self.animal_species[idx] == q.own_species_index as i16
            }
            16 => {
                // AnAnimal
                if (f & FLAG_HAS_GROUND_ANIMAL) == 0 {
                    return false;
                }
                let pat = self.animal_skin_pattern[idx] as usize;
                if q.skin_pattern_mask != 0 && (q.skin_pattern_mask & (1 << pat)) == 0 {
                    return false;
                }
                let h = self.animal_hue[idx];
                let b = self.animal_brightness[idx];
                let hue_ok = q.hue_max <= q.hue_min || (h > q.hue_min && h < q.hue_max);
                let br_ok = q.brightness_max <= q.brightness_min || (b > q.brightness_min && b < q.brightness_max);
                hue_ok && br_ok
            }
            1 => {
                // AColor
                if (f & FLAG_HAS_PLANT) != 0 {
                    let h = self.plant_hue[idx];
                    if h >= q.hue_min && h <= q.hue_max {
                        return true;
                    }
                }
                if (f & FLAG_HAS_GROUND_ANIMAL) != 0 {
                    let h = self.animal_hue[idx];
                    if h >= q.hue_min && h <= q.hue_max {
                        return true;
                    }
                }
                false
            }
            _ => false,
        }
    }

    pub fn search_instinct(&self, q: &NativeInstinctQuery) -> i32 {
        let width = self.width as i32;
        let height = self.height as i32;
        if width <= 0 || height <= 0 || q.cell_index < 0 || q.cell_index >= (width * height) {
            return -1;
        }

        let cx = q.cell_index / height;
        let cy = q.cell_index % height;

        let min_d = q.min_distance.max(0);
        let max_d = q.max_distance.max(min_d);

        let top = q.condition_sense != 1 || q.top != 0;
        let left = q.condition_sense != 1 || q.left != 0;
        let right = q.condition_sense != 1 || q.right != 0;
        let bottom = q.condition_sense != 1 || q.bottom != 0;

        if min_d == 0 {
            let idx = q.cell_index as usize;
            if self.check_cell_trigger(idx, q, 0) {
                return q.cell_index;
            }
        }

        let start_d = if min_d == 0 { 1 } else { min_d };
        for d in start_d..=max_d {
            let mut found = -1;
            if (d as usize) < RING_OFFSETS.len() {
                let offsets = RING_OFFSETS[d as usize];
                for &(ox, oy) in offsets {
                    let mut matched_idx = -1;
                    emit_ring_points(cx, cy, ox, oy, width, height, top, left, right, bottom, |idx| {
                        if self.check_cell_trigger(idx, q, d) {
                            matched_idx = idx as i32;
                            true
                        } else {
                            false
                        }
                    });
                    if matched_idx != -1 {
                        found = matched_idx;
                        break;
                    }
                }
            } else {
                for ox in -d..=d {
                    for oy in -d..=d {
                        let dist_sq = ox * ox + oy * oy;
                        if dist_sq >= (d * d - d) && dist_sq <= (d * d + d) {
                            let nx = cx + ox;
                            let ny = cy + oy;
                            if nx >= 0 && nx < width && ny >= 0 && ny < height {
                                let dir_ok = if q.condition_sense == 1 {
                                    (ox >= 0 || left) && (ox <= 0 || right) && (oy >= 0 || bottom) && (oy <= 0 || top)
                                } else {
                                    true
                                };
                                if dir_ok {
                                    let idx = (nx * height + ny) as usize;
                                    if self.check_cell_trigger(idx, q, d) {
                                        found = idx as i32;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    if found != -1 {
                        break;
                    }
                }
            }

            if found != -1 {
                return found;
            }
        }

        -1
    }

    pub fn search_instincts_parallel(&self, queries: &[NativeInstinctQuery], results: &mut [i32]) {
        let count = queries.len().min(results.len());
        results[..count]
            .par_iter_mut()
            .enumerate()
            .for_each(|(i, res)| {
                *res = self.search_instinct(&queries[i]);
            });
    }
}

#[cfg(test)]
mod tests_native_grid {
    use super::*;

    #[test]
    fn test_grid_coordinates_and_ring_search() {
        let mut grid = NativeGrid::new(10, 10);
        // Center cell: cx=5, cy=5 -> index = 5 * 10 + 5 = 55
        // Place a plant at distance 2: cx=5, cy=7 -> index = 5 * 10 + 7 = 57
        grid.set_plant(57, 1, 0.4, 0.6, 1.0, true, 0.2, true);

        let mut q = NativeInstinctQuery::default();
        q.cell_index = 55;
        q.min_distance = 1;
        q.max_distance = 5;
        q.condition_sense = 1; // See
        q.condition_object = 6; // Food
        q.top = 1;
        q.left = 1;
        q.right = 1;
        q.bottom = 1;
        q.can_eat_flags = 1; // Soft plant tissue

        let result = grid.search_instinct(&q);
        assert_eq!(result, 57, "Should find plant at cell 57 at distance 2");

        // Direction filter: look only bottom (cannot see top where y=7 is)
        q.top = 0;
        q.bottom = 1;
        q.left = 0;
        q.right = 0;
        let result_blind = grid.search_instinct(&q);
        assert_eq!(result_blind, -1, "Should not find plant when looking away");
    }

    #[test]
    fn test_parallel_instinct_batch() {
        let mut grid = NativeGrid::new(20, 20);
        // Place plants and animals
        grid.set_plant(105, 1, 0.5, 0.5, 1.0, true, 0.2, true);
        grid.set_animal(210, 2, 0.8, 0.5, 0, 1, 1.0, false); // species 2, fert receiver

        let mut queries = Vec::new();
        for i in 0..50 {
            let mut q = NativeInstinctQuery::default();
            q.cell_index = 100 + i;
            q.min_distance = 1;
            q.max_distance = 10;
            q.condition_sense = 1;
            q.condition_object = 6; // Food
            q.can_eat_flags = 1;
            q.top = 1;
            q.left = 1;
            q.right = 1;
            q.bottom = 1;
            queries.push(q);
        }

        let mut results = vec![-1; 50];
        grid.search_instincts_parallel(&queries, &mut results);
        // At least the one right next to 105 should find 105
        assert!(results.iter().any(|&r| r == 105), "Parallel search should find plant 105");
    }

    #[test]
    fn test_native_grid_daily_simulation() {
        let mut grid = NativeGrid::new(10, 10);
        let mut rng = FastRng::new(12345);

        // Cell 25: plant with energy and seeds
        grid.set_cell_env(25, 22.0, 0.6, 0.6, 0.8, 2, -1.0, 0.0, 0.2);
        grid.set_plant(25, 1, 0.3, 0.5, 1.2, true, 0.1, false);
        grid.set_plant_bio(25, 100.0, 0.8, 10, 100, 2);

        // Cell 26: detritus + decomposer fungus with cycle_duration 1
        grid.set_cell_env(26, 22.0, 0.5, 0.5, 0.2, 5, -1.0, 0.0, 0.1);
        grid.set_fungus(26, 1, false, 0.5, 1);

        // Cell 27: herbivore animal + grass
        grid.set_cell_env(27, 22.0, 0.5, 0.5, 0.5, 0, -1.0, 0.0, 0.1);
        grid.set_grass(27, 0.4, 0.6);
        grid.set_animal(27, 2, 0.1, 0.5, 0, 0, 1.0, false);
        grid.set_animal_bio(27, 100.0, 0.7, 0.9, 5, 80, 0);

        let summary = grid.step_simulation_day(0.05, 0.05, 0.05, 0.05, &mut rng);

        assert!(summary.total_plants >= 1, "Plants should survive or reproduce");
        assert!(summary.total_animals >= 1, "Animal should survive and graze");
        assert!(grid.nutrients[26] > 0.2, "Fungus decomposition must enrich soil nutrients");
    }
}




