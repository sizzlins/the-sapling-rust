//! C-ABI Foreign Function Interface for embedding in Unity C# via P/Invoke.

use crate::sim::World;

#[no_mangle]
pub unsafe extern "C" fn sapling_world_new(seed: u64) -> *mut World {
    Box::into_raw(Box::new(World::new(seed)))
}

#[no_mangle]
pub unsafe extern "C" fn sapling_world_free(world: *mut World) {
    if !world.is_null() {
        drop(Box::from_raw(world));
    }
}

#[no_mangle]
pub unsafe extern "C" fn sapling_world_populate(
    world: *mut World,
    plants: u32,
    herbivores: u32,
    extent: f32,
) {
    if let Some(w) = world.as_mut() {
        w.populate(plants as usize, herbivores as usize, extent);
    }
}

#[no_mangle]
pub unsafe extern "C" fn sapling_world_tick(world: *mut World) {
    if let Some(w) = world.as_mut() {
        w.tick();
    }
}

#[no_mangle]
pub unsafe extern "C" fn sapling_world_counts(
    world: *const World,
    out_plants: *mut u32,
    out_herbivores: *mut u32,
) {
    if let Some(w) = world.as_ref() {
        if !out_plants.is_null() {
            *out_plants = w.plants.len() as u32;
        }
        if !out_herbivores.is_null() {
            *out_herbivores = w.herbivores.len() as u32;
        }
    }
}

#[no_mangle]
pub unsafe extern "C" fn sapling_world_get_plants(
    world: *const World,
    out_x: *mut f32,
    out_y: *mut f32,
    out_scale: *mut f32,
    out_energy: *mut f32,
    max_len: u32,
) -> u32 {
    let w = match world.as_ref() {
        Some(w) => w,
        None => return 0,
    };
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
}

#[no_mangle]
pub unsafe extern "C" fn sapling_world_get_herbivores(
    world: *const World,
    out_x: *mut f32,
    out_y: *mut f32,
    out_energy: *mut f32,
    max_len: u32,
) -> u32 {
    let w = match world.as_ref() {
        Some(w) => w,
        None => return 0,
    };
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
}
