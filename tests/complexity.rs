//! Complexity proof for the simulation core.
//!
//! Run in release: `cargo test --release --test complexity`
//! (debug builds are ~50x slower and drown the signal in allocator noise)
//!
//! A timing test is worthless unless it can fail, so this file contains a control: the
//! same simulation driven through the O(n^2) food search must show quadratic scaling.
//! If the control ever looks linear, the harness is broken and the green result on the
//! hashed version means nothing.

use sapling_rust::sim::World;
use std::time::Instant;

/// Keep plant density constant across sizes, so "more organisms" means "more of the same
/// work", not "denser work". extent = sqrt(n) * K.
fn extent_for(plants: usize) -> f32 {
    (plants as f32).sqrt() * 8.0
}

fn build(plants: usize, seed: u64) -> World {
    let mut w = World::new(seed);
    w.populate(plants, plants / 4, extent_for(plants));
    w
}

/// Nanoseconds of CPU per organism per tick. Best of `repeats` to suppress scheduler noise.
fn ns_per_organism(plants: usize, ticks: usize, naive: bool, repeats: usize) -> f64 {
    let organisms = (plants + plants / 4) as f64;
    let mut best = f64::MAX;

    for _ in 0..repeats {
        let mut w = build(plants, 42);
        // Warm the allocator/branch predictors so the first size is not unfairly slow.
        if naive {
            w.tick_naive_seek();
        } else {
            w.tick();
        }

        let t0 = Instant::now();
        for _ in 0..ticks {
            if naive {
                w.tick_naive_seek();
            } else {
                w.tick();
            }
        }
        let ns = t0.elapsed().as_nanos() as f64 / (organisms * ticks as f64);
        if ns < best {
            best = ns;
        }
    }
    best
}

fn ratio(largest: f64, smallest: f64) -> f64 {
    largest / smallest
}

#[test]
fn time_is_sub_quadratic_with_spatial_hash() {
    let sizes = [5_000usize, 20_000, 80_000];
    let ticks = 10;

    let mut per_unit = Vec::new();
    for &n in &sizes {
        per_unit.push(ns_per_organism(n, ticks, false, 3));
        println!(
            "  hashed  n={:>6}  {:>8.1} ns/organism/tick",
            n,
            per_unit.last().unwrap()
        );
    }

    let growth = ratio(per_unit[2], per_unit[0]);
    println!("  hashed growth over 16x more organisms: {:.2}x", growth);

    // Linear would be ~1.0x, quadratic would be ~16x. Allow generous headroom for CI
    // noise and cache effects, but nothing close to quadratic.
    assert!(
        growth < 3.0,
        "hashed sim cost per organism grew {:.2}x for 16x the population - that is not sub-quadratic",
        growth
    );
}

#[test]
fn harness_detects_quadratic_when_search_is_naive() {
    // Same world, same tick, only the food search differs.
    let sizes = [400usize, 800, 1_600];
    let ticks = 4;

    let mut per_unit = Vec::new();
    for &n in &sizes {
        per_unit.push(ns_per_organism(n, ticks, true, 3));
        println!(
            "  naive   n={:>6}  {:>8.1} ns/organism/tick",
            n,
            per_unit.last().unwrap()
        );
    }

    let growth = ratio(per_unit[2], per_unit[0]);
    println!("  naive growth over 4x more organisms: {:.2}x", growth);

    assert!(
        growth > 2.5,
        "control failed: the quadratic search only grew {:.2}x per organism over a 4x \\
         population increase, so the timing harness cannot detect quadratic behaviour and \\
         the hashed result above is not evidence of anything",
        growth
    );
}

#[test]
fn spatial_hash_beats_linear_scan() {
    let n = 20_000;
    let ticks = 10;
    let hashed = ns_per_organism(n, ticks, false, 3);
    let naive = ns_per_organism(n, ticks, true, 3);
    println!("  n={}  hashed {:.1} vs naive {:.1} ns/organism ({:.1}x)", n, hashed, naive, naive / hashed);
    assert!(
        hashed < naive,
        "spatial hash ({} ns) should beat the linear scan ({} ns) at n={}",
        hashed,
        naive,
        n
    );
}

#[test]
fn space_is_linear_with_a_small_constant() {
    let sizes = [10_000usize, 100_000];
    let mut bytes_per_organism = Vec::new();

    for &n in &sizes {
        let mut w = build(n, 42);
        for _ in 0..5 {
            w.tick();
        }
        let organisms = w.organisms().max(1) as f64;
        let bpo = w.heap_bytes() as f64 / organisms;
        bytes_per_organism.push(bpo);
        println!(
            "  n={:>6}  {:>7.0} bytes/organism  (total {:.2} MB)",
            n,
            bpo,
            w.heap_bytes() as f64 / 1_048_576.0
        );
    }

    // Absolute ceiling: struct-of-arrays with f32 fields should stay far under this.
    assert!(
        bytes_per_organism[1] < 256.0,
        "{:.0} bytes/organism is too fat - expected a struct-of-arrays layout",
        bytes_per_organism[1]
    );

    // 10x the population must not cost meaningfully more per organism.
    let growth = bytes_per_organism[1] / bytes_per_organism[0];
    println!("  bytes/organism growth over 10x more organisms: {:.2}x", growth);
    assert!(
        growth < 2.0,
        "per-organism memory grew {:.2}x for 10x the population - space is not linear",
        growth
    );
}