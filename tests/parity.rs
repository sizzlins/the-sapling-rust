//! Parity tests for the 6 core ecosystem simulation mechanics.
//! Maps 1:1 to rows 1-6 in SPEC.md, verified deterministically with seed=42.

use sapling_rust::sim::World;

#[test]
fn plant_growth_trend() {
    let mut w = World::new(42);
    // 50 plants, 0 herbivores so nothing eats the plants
    w.populate(50, 0, 100.0);

    let initial_scale = w.plants.mean_scale();
    // 120 sim-seconds at dt=0.1 -> 1200 ticks
    for _ in 0..1200 {
        w.tick();
    }
    let final_scale = w.plants.mean_scale();
    let growth_ratio = (final_scale - initial_scale) / initial_scale;

    println!("plant_growth_trend: initial={:.3}, final={:.3}, growth={:.1}%",
             initial_scale, final_scale, growth_ratio * 100.0);

    assert!(growth_ratio > 0.10, "plants must grow over 120s (got {:.1}%)", growth_ratio * 100.0);
}

#[test]
fn plant_spread_radius() {
    let mut w = World::new(42);
    // Single parent plant in center with high energy and zero cooldown
    w.plants.push(50.0, 50.0, 0.95, 0.8, 0.04, 150.0);

    // Run until a sprout is born
    let mut sprout_found = false;
    for _ in 0..600 {
        w.tick();
        if w.plants.len() > 1 {
            sprout_found = true;
            let parent_x = 50.0f32;
            let parent_y = 50.0f32;
            let sprout_x = w.plants.x[1];
            let sprout_y = w.plants.y[1];
            let dist = ((sprout_x - parent_x).powi(2) + (sprout_y - parent_y).powi(2)).sqrt();
            println!("plant_spread_radius: sprout spawned at distance {:.2} (spread_radius={})", dist, w.spread_radius);
            assert!(dist <= w.spread_radius * 1.15, "sprout spawned too far: {}", dist);
            assert!(dist >= 0.5, "sprout spawned on top of parent: {}", dist);
            break;
        }
    }
    assert!(sprout_found, "plant should have produced at least one sprout");
}

#[test]
fn plant_mutation_drift() {
    let mut w = World::new(42);
    // Plant with fixed growth rate
    let parent_growth = 0.030f32;
    w.plants.push(50.0, 50.0, 0.99, 0.8, parent_growth, 200.0);

    // Let it reproduce
    let mut mutated = false;
    for _ in 0..600 {
        w.tick();
        if w.plants.len() > 1 {
            let child_growth = w.plants.growth_rate[1];
            let drift = (child_growth - parent_growth).abs() / parent_growth;
            println!("plant_mutation_drift: parent_rate={}, child_rate={}, drift={:.2}%",
                     parent_growth, child_growth, drift * 100.0);
            assert!(drift > 0.0, "child should have non-zero mutation drift");
            assert!(drift < 0.15, "child mutation drift exceeded tolerance: {:.2}%", drift * 100.0);
            mutated = true;
            break;
        }
    }
    assert!(mutated, "expected child to evaluate mutation drift");
}

#[test]
fn herbivore_eats() {
    let mut w = World::new(42);
    // Place plant close to herbivore
    w.plants.push(50.0, 50.0, 1.0, 0.8, 0.02, 100.0);
    w.herbivores.push(52.0, 50.0, 0.5, 100.0);

    let initial_energy = w.herbivores.energy[0];
    let mut eaten = false;

    // Contact and eat within 50 ticks (5 sim-seconds)
    for _ in 0..50 {
        w.tick();
        if w.herbivores.plants_eaten[0] > 0 || w.herbivores.energy[0] > initial_energy {
            eaten = true;
            println!("herbivore_eats: ate plant within 5 sim-seconds, new energy={:.3}", w.herbivores.energy[0]);
            break;
        }
    }
    assert!(eaten, "herbivore should seek and eat adjacent plant within 5s");
}

#[test]
fn herbivore_repro() {
    let mut w = World::new(42);
    // Herbivore with high energy ready to reproduce
    w.herbivores.push(50.0, 50.0, 0.95, 100.0);

    let mut reproduced = false;
    for _ in 0..50 {
        w.tick();
        if w.herbivores.len() == 2 {
            reproduced = true;
            println!("herbivore_repro: produced 1 offspring, total={}", w.herbivores.len());
            assert_eq!(w.herbivores.len(), 2, "reproduction should yield exactly 1 offspring");
            assert!(w.herbivores.breed_cooldown[0] > 0.0, "parent cooldown should be active");
            break;
        }
    }
    assert!(reproduced, "herbivore at high energy should reproduce");
}

#[test]
fn death_occurs() {
    let mut w = World::new(42);
    // Plant with low max age, starving herbivore with low energy and no plants
    w.plants.push(10.0, 10.0, 0.2, 0.5, 0.01, 2.0); // max_age = 2.0 (20 ticks)
    w.herbivores.push(90.0, 90.0, 0.02, 50.0); // low energy, no food -> starvation

    let mut plant_died = false;
    let mut herbivore_died = false;

    // Run for 300 sim-seconds (3000 ticks)
    for _ in 0..3000 {
        w.tick();
        if w.deaths_plants > 0 {
            plant_died = true;
        }
        if w.deaths_herbivores > 0 {
            herbivore_died = true;
        }
        if plant_died && herbivore_died {
            break;
        }
    }

    println!("death_occurs: plant_died={}, herbivore_died={}, deaths_p={}, deaths_h={}",
             plant_died, herbivore_died, w.deaths_plants, w.deaths_herbivores);
    assert!(plant_died, "plant should die of old age");
    assert!(herbivore_died, "herbivore should die of starvation when no food is in reach");
}
