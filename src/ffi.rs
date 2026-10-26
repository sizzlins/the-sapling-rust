//! C-ABI Foreign Function Interface for embedding in Unity C# via P/Invoke.
//! Fully synchronized via RwLock to eliminate multi-threaded data races
//! between background jump workers and main-thread GPU instancing.
//! All calls are wrapped with catch_unwind to prevent unhandled aborts (0xc0000409).

use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::RwLock;

use crate::sim::{
    CreatureDecision, DaySimulationSummary, FastRng, InstanceMatrix, NativeGrid, NativeInstinctQuery,
    WarpStats, World,
};

pub struct WorldHandle {
    pub inner: RwLock<World>,
}

fn init_panic_hook() {
    static PANIC_HOOK_INIT: std::sync::Once = std::sync::Once::new();
    PANIC_HOOK_INIT.call_once(|| {
        std::panic::set_hook(Box::new(|info| {
            let msg = format!("=== SAPLING RUST PANIC ===\n{}\n", info);
            let _ = std::fs::write("D:/The_Sapling/The Sapling/sapling_rust_panic.txt", msg);
        }));
    });
}

#[inline]
unsafe fn with_read<F, R>(handle: *const WorldHandle, default: R, f: F) -> R
where
    F: FnOnce(&World) -> R,
{
    if handle.is_null() {
        return default;
    }
    let res = catch_unwind(AssertUnwindSafe(|| {
        let lock = match (*handle).inner.read() {
            Ok(l) => l,
            Err(poisoned) => poisoned.into_inner(),
        };
        f(&*lock)
    }));
    res.unwrap_or(default)
}

#[inline]
unsafe fn with_write<F, R>(handle: *mut WorldHandle, default: R, f: F) -> R
where
    F: FnOnce(&mut World) -> R,
{
    if handle.is_null() {
        return default;
    }
    let res = catch_unwind(AssertUnwindSafe(|| {
        let mut lock = match (*handle).inner.write() {
            Ok(l) => l,
            Err(poisoned) => poisoned.into_inner(),
        };
        f(&mut *lock)
    }));
    res.unwrap_or(default)
}

#[no_mangle]
pub unsafe extern "C" fn sapling_world_new(seed: u64) -> *mut WorldHandle {
    init_panic_hook();
    let res = catch_unwind(AssertUnwindSafe(|| {
        Box::into_raw(Box::new(WorldHandle {
            inner: RwLock::new(World::new(seed)),
        }))
    }));
    res.unwrap_or(std::ptr::null_mut())
}

#[no_mangle]
pub unsafe extern "C" fn sapling_world_free(handle: *mut WorldHandle) {
    if !handle.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| {
            drop(Box::from_raw(handle));
        }));
    }
}

#[no_mangle]
pub unsafe extern "C" fn sapling_world_populate(
    handle: *mut WorldHandle,
    plants: u32,
    herbivores: u32,
    extent: f32,
) {
    with_write(handle, (), |w| {
        w.populate(plants as usize, herbivores as usize, extent);
    });
}

#[no_mangle]
pub unsafe extern "C" fn sapling_world_tick(handle: *mut WorldHandle) {
    with_write(handle, (), |w| {
        w.tick();
    });
}

#[no_mangle]
pub unsafe extern "C" fn sapling_world_rebuild_grid(handle: *mut WorldHandle) {
    with_write(handle, (), |w| {
        w.rebuild_grid();
    });
}

#[no_mangle]
pub unsafe extern "C" fn sapling_world_counts(
    handle: *const WorldHandle,
    out_plants: *mut u32,
    out_herbivores: *mut u32,
) {
    with_read(handle, (), |w| {
        if !out_plants.is_null() {
            *out_plants = w.plants.len() as u32;
        }
        if !out_herbivores.is_null() {
            *out_herbivores = w.herbivores.len() as u32;
        }
    });
}

#[no_mangle]
pub unsafe extern "C" fn sapling_world_get_plants(
    handle: *const WorldHandle,
    out_x: *mut f32,
    out_y: *mut f32,
    out_scale: *mut f32,
    out_energy: *mut f32,
    max_len: u32,
) -> u32 {
    with_read(handle, 0, |w| {
        let count = w.plants.len().min(max_len as usize);
        if !out_x.is_null() {
            std::ptr::copy_nonoverlapping(w.plants.x.as_ptr(), out_x, count);
        }
        if !out_y.is_null() {
            std::ptr::copy_nonoverlapping(w.plants.y.as_ptr(), out_y, count);
        }
        if !out_scale.is_null() {
            std::ptr::copy_nonoverlapping(w.plants.scale.as_ptr(), out_scale, count);
        }
        if !out_energy.is_null() {
            std::ptr::copy_nonoverlapping(w.plants.energy.as_ptr(), out_energy, count);
        }
        count as u32
    })
}

#[no_mangle]
pub unsafe extern "C" fn sapling_world_get_herbivores(
    handle: *const WorldHandle,
    out_x: *mut f32,
    out_y: *mut f32,
    out_energy: *mut f32,
    max_len: u32,
) -> u32 {
    with_read(handle, 0, |w| {
        let count = w.herbivores.len().min(max_len as usize);
        if !out_x.is_null() {
            std::ptr::copy_nonoverlapping(w.herbivores.x.as_ptr(), out_x, count);
        }
        if !out_y.is_null() {
            std::ptr::copy_nonoverlapping(w.herbivores.y.as_ptr(), out_y, count);
        }
        if !out_energy.is_null() {
            std::ptr::copy_nonoverlapping(w.herbivores.energy.as_ptr(), out_energy, count);
        }
        count as u32
    })
}

#[no_mangle]
pub unsafe extern "C" fn sapling_find_nearest_plants_batch(
    handle: *const WorldHandle,
    query_x: *const f32,
    query_y: *const f32,
    max_dist: f32,
    out_indices: *mut i32,
    count: u32,
) -> u32 {
    if query_x.is_null() || query_y.is_null() || out_indices.is_null() || count == 0 {
        return 0;
    }
    with_read(handle, 0, |w| {
        let qx = std::slice::from_raw_parts(query_x, count as usize);
        let qy = std::slice::from_raw_parts(query_y, count as usize);
        let out = std::slice::from_raw_parts_mut(out_indices, count as usize);
        w.find_nearest_plants_parallel(qx, qy, max_dist, out);
        count
    })
}

#[no_mangle]
pub unsafe extern "C" fn sapling_world_warp(
    handle: *mut WorldHandle,
    years: u32,
    out_stats: *mut WarpStats,
) -> u32 {
    with_write(handle, 0, |w| {
        let stats = w.warp(years);
        if !out_stats.is_null() {
            *out_stats = stats;
        }
        1
    })
}

#[no_mangle]
pub unsafe extern "C" fn sapling_timeline_push(
    handle: *mut WorldHandle,
    day: u32,
    total_plants: u32,
    total_animals: u32,
    species_counts: *const u16,
    species_len: u32,
) -> u32 {
    with_write(handle, 0, |w| {
        let counts = if species_counts.is_null() || species_len == 0 {
            &[]
        } else {
            std::slice::from_raw_parts(species_counts, species_len as usize)
        };
        w.timeline.push(day, total_plants, total_animals, counts);
        w.timeline.len() as u32
    })
}

#[no_mangle]
pub unsafe extern "C" fn sapling_timeline_len(handle: *const WorldHandle) -> u32 {
    with_read(handle, 0, |w| w.timeline.len() as u32)
}

#[no_mangle]
pub unsafe extern "C" fn sapling_timeline_get(
    handle: *const WorldHandle,
    day: u32,
    out_counts: *mut u16,
    max_len: u32,
) -> u32 {
    with_read(handle, 0, |w| {
        if let Some(snap) = w.timeline.get(day) {
            let count = snap.species_counts.len().min(max_len as usize);
            if !out_counts.is_null() && count > 0 {
                std::ptr::copy_nonoverlapping(snap.species_counts.as_ptr(), out_counts, count);
            }
            count as u32
        } else {
            0
        }
    })
}

#[no_mangle]
pub unsafe extern "C" fn sapling_creatures_evaluate_batch(
    handle: *const WorldHandle,
    xs: *const f32,
    ys: *const f32,
    energies: *const f32,
    is_carnivore: *const u8,
    count: u32,
    max_search: f32,
    out_decisions: *mut CreatureDecision,
) -> u32 {
    if xs.is_null() || ys.is_null() || energies.is_null() || out_decisions.is_null() || count == 0 {
        return 0;
    }
    with_read(handle, 0, |w| {
        let s_xs = std::slice::from_raw_parts(xs, count as usize);
        let s_ys = std::slice::from_raw_parts(ys, count as usize);
        let s_en = std::slice::from_raw_parts(energies, count as usize);
        let s_carn = if is_carnivore.is_null() {
            &[]
        } else {
            std::slice::from_raw_parts(is_carnivore, count as usize)
        };
        let s_out = std::slice::from_raw_parts_mut(out_decisions, count as usize);
        w.evaluate_creatures_parallel(s_xs, s_ys, s_en, s_carn, max_search, s_out);
        count
    })
}

#[no_mangle]
pub unsafe extern "C" fn sapling_compute_billboards_batch(
    handle: *const WorldHandle,
    cam_x: f32,
    cam_y: f32,
    cam_z: f32,
    max_dist: f32,
    out_matrices: *mut InstanceMatrix,
    max_len: u32,
) -> u32 {
    if out_matrices.is_null() || max_len == 0 {
        return 0;
    }
    with_read(handle, 0, |w| {
        let slice = std::slice::from_raw_parts_mut(out_matrices, max_len as usize);
        w.compute_billboard_matrices_parallel(cam_x, cam_y, cam_z, max_dist, slice) as u32
    })
}

pub struct GridHandle {
    pub inner: RwLock<NativeGrid>,
}

#[no_mangle]
pub unsafe extern "C" fn sapling_grid_create(width: u32, height: u32) -> *mut GridHandle {
    init_panic_hook();
    let res = catch_unwind(AssertUnwindSafe(|| {
        Box::into_raw(Box::new(GridHandle {
            inner: RwLock::new(NativeGrid::new(width as usize, height as usize)),
        }))
    }));
    res.unwrap_or(std::ptr::null_mut())
}

#[no_mangle]
pub unsafe extern "C" fn sapling_grid_free(handle: *mut GridHandle) {
    if !handle.is_null() {
        let _ = catch_unwind(AssertUnwindSafe(|| {
            drop(Box::from_raw(handle));
        }));
    }
}

#[no_mangle]
pub unsafe extern "C" fn sapling_grid_clear_organisms(handle: *mut GridHandle) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if let Ok(mut g) = (*handle).inner.write() {
            g.clear_organisms();
        }
    }));
}

#[no_mangle]
pub unsafe extern "C" fn sapling_grid_set_terrain(
    handle: *mut GridHandle,
    idx: u32,
    ocean_depth: f32,
    is_water: u8,
) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if let Ok(mut g) = (*handle).inner.write() {
            g.set_terrain(idx as usize, ocean_depth, is_water != 0);
        }
    }));
}

#[no_mangle]
pub unsafe extern "C" fn sapling_grid_set_plant(
    handle: *mut GridHandle,
    idx: u32,
    species: i16,
    hue: f32,
    brightness: f32,
    leaf_height: f32,
    fruit_ripe: u8,
    fruit_hue: f32,
    bark_none: u8,
) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if let Ok(mut g) = (*handle).inner.write() {
            g.set_plant(
                idx as usize,
                species,
                hue,
                brightness,
                leaf_height,
                fruit_ripe != 0,
                fruit_hue,
                bark_none != 0,
            );
        }
    }));
}

#[no_mangle]
pub unsafe extern "C" fn sapling_grid_set_alga(
    handle: *mut GridHandle,
    idx: u32,
    is_plankton: u8,
    hue: f32,
    brightness: f32,
) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if let Ok(mut g) = (*handle).inner.write() {
            g.set_alga(idx as usize, is_plankton != 0, hue, brightness);
        }
    }));
}

#[no_mangle]
pub unsafe extern "C" fn sapling_grid_set_grass(
    handle: *mut GridHandle,
    idx: u32,
    hue: f32,
    brightness: f32,
) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if let Ok(mut g) = (*handle).inner.write() {
            g.set_grass(idx as usize, hue, brightness);
        }
    }));
}

#[no_mangle]
pub unsafe extern "C" fn sapling_grid_set_animal(
    handle: *mut GridHandle,
    idx: u32,
    species: i16,
    hue: f32,
    brightness: f32,
    skin_pattern: u8,
    fert_flags: u8,
    step_loudness: f32,
    is_glowing: u8,
) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if let Ok(mut g) = (*handle).inner.write() {
            g.set_animal(
                idx as usize,
                species,
                hue,
                brightness,
                skin_pattern,
                fert_flags,
                step_loudness,
                is_glowing != 0,
            );
        }
    }));
}

#[no_mangle]
pub unsafe extern "C" fn sapling_grid_set_carrion(handle: *mut GridHandle, idx: u32) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if let Ok(mut g) = (*handle).inner.write() {
            g.set_carrion(idx as usize);
        }
    }));
}

#[no_mangle]
pub unsafe extern "C" fn sapling_grid_set_cell_env(
    handle: *mut GridHandle,
    idx: u32,
    temp: f32,
    shallow_w: f32,
    deep_w: f32,
    nutrients: f32,
    detritus: u16,
    ocean_depth: f32,
    steepness: f32,
    wind: f32,
) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if let Ok(mut g) = (*handle).inner.write() {
            g.set_cell_env(
                idx as usize,
                temp,
                shallow_w,
                deep_w,
                nutrients,
                detritus,
                ocean_depth,
                steepness,
                wind,
            );
        }
    }));
}

#[no_mangle]
pub unsafe extern "C" fn sapling_grid_set_fungus(
    handle: *mut GridHandle,
    idx: u32,
    species: i16,
    is_parasite: u8,
    energy: f32,
    cycle_duration: u16,
) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if let Ok(mut g) = (*handle).inner.write() {
            g.set_fungus(
                idx as usize,
                species,
                is_parasite != 0,
                energy,
                cycle_duration,
            );
        }
    }));
}

#[no_mangle]
pub unsafe extern "C" fn sapling_grid_set_plant_bio(
    handle: *mut GridHandle,
    idx: u32,
    health: f32,
    energy: f32,
    age: u16,
    max_age: u16,
    seeds: u8,
) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if let Ok(mut g) = (*handle).inner.write() {
            g.set_plant_bio(idx as usize, health, energy, age, max_age, seeds);
        }
    }));
}

#[no_mangle]
pub unsafe extern "C" fn sapling_grid_set_animal_bio(
    handle: *mut GridHandle,
    idx: u32,
    health: f32,
    energy: f32,
    water: f32,
    age: u16,
    max_age: u16,
    diet: u8,
) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if let Ok(mut g) = (*handle).inner.write() {
            g.set_animal_bio(idx as usize, health, energy, water, age, max_age, diet);
        }
    }));
}

#[no_mangle]
pub unsafe extern "C" fn sapling_grid_step_day(
    handle: *mut GridHandle,
    mut_algae: f32,
    mut_plants: f32,
    mut_animals: f32,
    mut_fungi: f32,
    seed: u64,
    out_summary: *mut DaySimulationSummary,
) -> i32 {
    if handle.is_null() {
        return -1;
    }
    let res = catch_unwind(AssertUnwindSafe(|| {
        if let Ok(mut g) = (*handle).inner.write() {
            let mut rng = FastRng::new(seed);
            let summary = g.step_simulation_day(mut_algae, mut_plants, mut_animals, mut_fungi, &mut rng);
            if !out_summary.is_null() {
                *out_summary = summary;
            }
            0
        } else {
            -2
        }
    }));
    res.unwrap_or(-3)
}

#[no_mangle]
pub unsafe extern "C" fn sapling_grid_get_detritus(handle: *const GridHandle, idx: u32) -> u16 {
    if handle.is_null() {
        return 0;
    }
    let res = catch_unwind(AssertUnwindSafe(|| {
        if let Ok(g) = (*handle).inner.read() {
            let i = idx as usize;
            if i < g.detritus.len() {
                g.detritus[i]
            } else {
                0
            }
        } else {
            0
        }
    }));
    res.unwrap_or(0)
}

#[no_mangle]
pub unsafe extern "C" fn sapling_grid_get_nutrients(handle: *const GridHandle, idx: u32) -> f32 {
    if handle.is_null() {
        return 0.0;
    }
    let res = catch_unwind(AssertUnwindSafe(|| {
        if let Ok(g) = (*handle).inner.read() {
            let i = idx as usize;
            if i < g.nutrients.len() {
                g.nutrients[i]
            } else {
                0.0
            }
        } else {
            0.0
        }
    }));
    res.unwrap_or(0.0)
}

#[no_mangle]
pub unsafe extern "C" fn sapling_grid_search_instinct(
    handle: *const GridHandle,
    query: *const NativeInstinctQuery,
) -> i32 {
    if handle.is_null() || query.is_null() {
        return -1;
    }
    let res = catch_unwind(AssertUnwindSafe(|| {
        if let Ok(g) = (*handle).inner.read() {
            g.search_instinct(&*query)
        } else {
            -1
        }
    }));
    res.unwrap_or(-1)
}

#[no_mangle]
pub unsafe extern "C" fn sapling_grid_search_instincts_batch(
    handle: *const GridHandle,
    queries: *const NativeInstinctQuery,
    count: u32,
    out_results: *mut i32,
) -> u32 {
    if handle.is_null() || queries.is_null() || out_results.is_null() || count == 0 {
        return 0;
    }
    let res = catch_unwind(AssertUnwindSafe(|| {
        if let Ok(g) = (*handle).inner.read() {
            let q_slice = std::slice::from_raw_parts(queries, count as usize);
            let r_slice = std::slice::from_raw_parts_mut(out_results, count as usize);
            g.search_instincts_parallel(q_slice, r_slice);
            count
        } else {
            0
        }
    }));
    res.unwrap_or(0)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_c_abi_lifecycle() {
        unsafe {
            let world = sapling_world_new(12345);
            assert!(!world.is_null());

            sapling_world_populate(world, 100, 20, 200.0);

            let mut plant_count = 0u32;
            let mut herb_count = 0u32;
            sapling_world_counts(world, &mut plant_count, &mut herb_count);
            assert_eq!(plant_count, 100);
            assert_eq!(herb_count, 20);

            sapling_world_tick(world);

            let mut xs = [0.0f32; 100];
            let mut ys = [0.0f32; 100];
            let mut scales = [0.0f32; 100];
            let mut energies = [0.0f32; 100];
            let retrieved = sapling_world_get_plants(
                world,
                xs.as_mut_ptr(),
                ys.as_mut_ptr(),
                scales.as_mut_ptr(),
                energies.as_mut_ptr(),
                100,
            );
            assert!(retrieved > 0);

            sapling_world_free(world);
        }
    }

    #[test]
    fn test_c_abi_warp() {
        unsafe {
            let world = sapling_world_new(9999);
            sapling_world_populate(world, 50, 10, 100.0);
            let mut stats = WarpStats::default();
            let ok = sapling_world_warp(world, 1000, &mut stats);
            assert_eq!(ok, 1);
            assert!(stats.active_plants > 0);
            sapling_world_free(world);
        }
    }

    #[test]
    fn test_c_abi_warp_zero_animals() {
        unsafe {
            let world = sapling_world_new(1234);
            sapling_world_populate(world, 50, 0, 100.0);
            let mut stats = WarpStats::default();
            let ok = sapling_world_warp(world, 1000, &mut stats);
            assert_eq!(ok, 1);
            assert!(stats.active_plants > 0);
            assert_eq!(stats.animal_mutations, 0);
            assert_eq!(stats.animal_births, 0);
            assert_eq!(stats.active_herbivores, 0);
            sapling_world_free(world);
        }
    }

    #[test]
    fn test_c_abi_timeline() {
        unsafe {
            let world = sapling_world_new(555);
            let species = [12u16, 34, 56];
            let len = sapling_timeline_push(world, 1, 100, 20, species.as_ptr(), 3);
            assert_eq!(len, 1);
            assert_eq!(sapling_timeline_len(world), 1);

            let mut out = [0u16; 3];
            let read = sapling_timeline_get(world, 1, out.as_mut_ptr(), 3);
            assert_eq!(read, 3);
            assert_eq!(out, [12, 34, 56]);

            sapling_world_free(world);
        }
    }

    #[test]
    fn test_c_abi_creature_eval() {
        unsafe {
            let world = sapling_world_new(777);
            sapling_world_populate(world, 50, 0, 100.0);

            let xs = [10.0f32, 20.0f32];
            let ys = [10.0f32, 20.0f32];
            let energies = [0.3f32, 0.9f32];
            let is_carn = [0u8, 0u8];
            let mut decisions = [CreatureDecision::default(), CreatureDecision::default()];

            let evaluated = sapling_creatures_evaluate_batch(
                world,
                xs.as_ptr(),
                ys.as_ptr(),
                energies.as_ptr(),
                is_carn.as_ptr(),
                2,
                100.0,
                decisions.as_mut_ptr(),
            );
            assert_eq!(evaluated, 2);
            assert_eq!(decisions[0].action, 1); // MoveToFood
            assert_eq!(decisions[1].action, 2); // Mate

            sapling_world_free(world);
        }
    }

    #[test]
    fn test_c_abi_billboards() {
        unsafe {
            let world = sapling_world_new(888);
            sapling_world_populate(world, 20, 0, 50.0);

            let mut matrices = [InstanceMatrix::default(); 50];
            let count = sapling_compute_billboards_batch(
                world,
                0.0,
                0.0,
                0.0,
                200.0,
                matrices.as_mut_ptr(),
                50,
            );
            assert!(count > 0);
            assert_ne!(matrices[0].m[15], 0.0);

            sapling_world_free(world);
        }
    }

    #[test]
    fn test_c_abi_grid_instinct() {
        unsafe {
            let grid = sapling_grid_create(15, 15);
            assert!(!grid.is_null());

            // Center: cx=4, cy=10 -> idx = 4 * 15 + 10 = 70
            // Plant at distance 2: cx=4, cy=12 -> idx = 4 * 15 + 12 = 72
            sapling_grid_set_plant(grid, 72, 1, 0.5, 0.5, 1.0, 1, 0.2, 1);

            let mut q = NativeInstinctQuery::default();
            q.cell_index = 70;
            q.min_distance = 1;
            q.max_distance = 8;
            q.condition_sense = 1; // See
            q.condition_object = 6; // Food
            q.can_eat_flags = 1;
            q.top = 1;
            q.left = 1;
            q.right = 1;
            q.bottom = 1;

            let found = sapling_grid_search_instinct(grid, &q);
            assert_eq!(found, 72);

            let mut out = [-1i32];
            let batch_count = sapling_grid_search_instincts_batch(grid, &q, 1, out.as_mut_ptr());
            assert_eq!(batch_count, 1);
            assert_eq!(out[0], 72);

            sapling_grid_free(grid);
        }
    }
}
