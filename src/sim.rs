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
}
