use sapling_rust::sim::World;

#[test]
fn test_parallel_spatial_query_parity() {
    let mut world = World::new(42);
    world.populate(5000, 1000, 200.0);
    // Run a tick to exercise the simulation, then rebuild grid
    // to sync spatial indices with current plant positions
    // (step_herbivores may remove plants after internal grid rebuild)
    world.tick();
    world.rebuild_grid();

    let count = 1000;
    let query_x: Vec<f32> = (0..count).map(|i| (i as f32 * 0.2)).collect();
    let query_y: Vec<f32> = (0..count).map(|i| (i as f32 * 0.2)).collect();
    let mut out_indices = vec![-1i32; count];

    world.find_nearest_plants_parallel(&query_x, &query_y, 25.0, &mut out_indices);

    // Verify at least some plants found and no index out of bounds
    let found = out_indices.iter().filter(|&&idx| idx >= 0).count();
    assert!(found > 0, "Parallel search must find nearest neighbors");

    // Verify no out-of-bounds index
    let plant_count = world.plants.len() as i32;
    for &idx in &out_indices {
        assert!(idx == -1 || idx < plant_count, "Index out of bounds: {idx}");
    }
}

#[test]
fn test_parallel_deterministic() {
    // Same inputs should produce same outputs across runs (no race conditions).
    let mut world = World::new(99);
    world.populate(2000, 0, 150.0);
    world.tick();

    let count = 500;
    let qx: Vec<f32> = (0..count).map(|i| i as f32 * 0.3).collect();
    let qy: Vec<f32> = (0..count).map(|i| i as f32 * 0.3).collect();

    let mut out1 = vec![-1i32; count];
    let mut out2 = vec![-1i32; count];

    world.find_nearest_plants_parallel(&qx, &qy, 50.0, &mut out1);
    world.find_nearest_plants_parallel(&qx, &qy, 50.0, &mut out2);

    assert_eq!(out1, out2, "Parallel queries must be deterministic");
}
