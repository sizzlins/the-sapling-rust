//! Native L1/L2-cache-resident instinct & food ring scanner for The Sapling.
//!
//! Traverses the exact retail `BAND[1..=20]` offset table from `crate::band`
//! directly in registers without materializing intermediate index buffers,
//! filtering empty cells via a compact `flags: [u32; N]` bitmask before
//! touching per-cell SoA fields.

use crate::band::{BAND, FLAG_BOTTOM, FLAG_LEFT, FLAG_RIGHT, FLAG_TOP, MAX_DISTANCE};

// Cell flag bits (shared verbatim with C# FastSimPatches).
pub const BIT_PLANT: u32 = 1 << 0;
pub const BIT_PLANT_BARK: u32 = 1 << 1;
pub const BIT_PLANT_NECTAR: u32 = 1 << 2;
pub const BIT_PLANT_SEED: u32 = 1 << 3;
pub const BIT_PLANT_NUT: u32 = 1 << 4;
pub const BIT_PLANT_FRUIT: u32 = 1 << 5;
pub const BIT_GRASS: u32 = 1 << 6;
pub const BIT_ALGA: u32 = 1 << 7;
pub const BIT_PLANKTON: u32 = 1 << 8;
pub const BIT_DEEP_SEA: u32 = 1 << 9;
pub const BIT_FUNGUS: u32 = 1 << 10;
pub const BIT_CARRION: u32 = 1 << 11;
pub const BIT_CARRION_OLD: u32 = 1 << 12;
pub const BIT_NEST_ENCLOSED: u32 = 1 << 13;
pub const BIT_ANIMAL_GROUND: u32 = 1 << 14;
pub const BIT_ANIMAL_CANOPY: u32 = 1 << 15;
pub const BIT_ANIMAL_AIR: u32 = 1 << 16;
pub const BIT_ANIMAL_SURFACE: u32 = 1 << 17;
pub const BIT_ANIMAL_WATER: u32 = 1 << 18;
pub const BIT_ANIMAL_DEEP: u32 = 1 << 19;
pub const BIT_HAS_EGG: u32 = 1 << 20;
pub const BIT_STEP_WALK_FROZEN_OK: u32 = 1 << 21;
pub const BIT_GA_HIDING_OR_CAMO: u32 = 1 << 22;
pub const BIT_GA_IS_CHILD: u32 = 1 << 23;

// DetailedFoodType bit positions (matches DetailedFoodType enum values).
pub const FOOD_PLANKTON: u32 = 1 << 1;
pub const FOOD_SOFT_PLANT: u32 = 1 << 2;
pub const FOOD_SAP: u32 = 1 << 3;
pub const FOOD_BARK: u32 = 1 << 4;
pub const FOOD_NECTAR: u32 = 1 << 5;
pub const FOOD_SEED: u32 = 1 << 6;
pub const FOOD_NUT: u32 = 1 << 7;
pub const FOOD_FRUIT: u32 = 1 << 8;
pub const FOOD_WHOLE_ANIMAL: u32 = 1 << 9;
pub const FOOD_FRESH_MEAT: u32 = 1 << 10;
pub const FOOD_OLD_MEAT: u32 = 1 << 11;
pub const FOOD_EGG: u32 = 1 << 12;
pub const FOOD_BLOOD: u32 = 1 << 13;
pub const FOOD_MILK: u32 = 1 << 14;
pub const FOOD_FUNGUS: u32 = 1 << 15;
pub const FOOD_GRASS: u32 = 1 << 16;
pub const FOOD_MARINE_SNOW: u32 = 1 << 17;

pub const MODE_FOOD: i32 = 1;
pub const MODE_ANIMAL_GROUND: i32 = 2;
pub const MODE_COLOR_GROUND: i32 = 3;
pub const MODE_PLANT: i32 = 4;
pub const MODE_ALGA: i32 = 5;
pub const MODE_FIRST_FLAG: i32 = 6;

/// Pinned SoA view of a single WorldArea for the current day.
#[repr(C)]
pub struct VisionGrid {
    pub size_x: i32,
    pub size_y: i32,
    pub cell_count: i32,
    pub flags: *const u32,

    // Plant fields
    pub plant_hue: *const f32,
    pub plant_bright: *const f32,
    pub plant_fruit_hue: *const f32,
    pub plant_flower_hue: *const f32,
    pub plant_leaf_h: *const f32,
    pub plant_seed_h: *const f32,
    pub plant_nut_h: *const f32,
    pub plant_fruit_h: *const f32,
    pub plant_fruit_shape: *const u8,

    // Grass fields
    pub grass_hue: *const f32,
    pub grass_bright: *const f32,

    // Alga fields (alga ?? plankton)
    pub alga_hue: *const f32,
    pub alga_bright: *const f32,

    // Carrion & Egg fields
    pub carrion_species: *const i32,
    /// Single-egg species index, -1 if none/unknown, -2 if multiple eggs (C# fallback).
    pub egg_species: *const i32,

    // Ground animal fields
    pub ga_hue: *const f32,
    pub ga_bright: *const f32,
    pub ga_sec_hue: *const f32,
    pub ga_sec_bright: *const f32,
    pub ga_salient_hue: *const f32,
    pub ga_salient_bright: *const f32,
    pub ga_skin_pat: *const u8,
    pub ga_species: *const i32,
    pub ga_size: *const i32,
    pub ga_lf_idx: *const i32,
}

/// Query parameters for one ring scan (stack-allocated in C# and passed by ref).
#[repr(C)]
pub struct VisionQuery {
    pub mode: i32,
    pub center_x: i32,
    pub center_y: i32,
    pub start_d: i32,
    pub start_slot: i32,
    pub max_d: i32,
    pub dir_mask: u8,
    pub invert_soft: u8,
    pub invert_fruit: u8,
    pub invert_whole: u8,

    pub can_fly_or_climb: u8,
    pub carn_is_child: u8,
    pub prudent: u8,
    pub _pad0: u8,

    pub food_type_mask: u32,
    pub direct_food_flags: u32,
    pub exclude_fruit_shapes_mask: u32,
    pub exclude_skin_pats_mask: u32,
    pub inst_skin_pat_mask: u32,
    pub flag_req_mask: u32,
    pub flag_req_expected: u32,

    pub eat_height_x10: f32,
    pub carn_species: i32,
    pub carn_dict_species: i32,
    pub carn_size: i32,
    pub parent_lf_idx: i32,

    // Generic ranges:
    // - For MODE_ANIMAL_GROUND / MODE_COLOR_GROUND / MODE_PLANT / MODE_ALGA:
    //   r1 = conditionRange, r2 = conditionAdditionalRange,
    //   r3 = conditionAdditionalRange2, r4 = conditionAdditionalRange3.
    // - For MODE_FOOD:
    //   r1 = softPlantHue, r2 = softPlantBright, r3 = fruitHue,
    //   w1 = wholeHue, w2 = wholeBright, w3 = wholeSecHue, w4 = wholeSecBright.
    pub r1_x: f32,
    pub r1_y: f32,
    pub r2_x: f32,
    pub r2_y: f32,
    pub r3_x: f32,
    pub r3_y: f32,
    pub r4_x: f32,
    pub r4_y: f32,

    pub w1_x: f32,
    pub w1_y: f32,
    pub w2_x: f32,
    pub w2_y: f32,
    pub w3_x: f32,
    pub w3_y: f32,
    pub w4_x: f32,
    pub w4_y: f32,
}

#[inline(always)]
fn in_range_inc(v: f32, min: f32, max: f32) -> bool {
    !(v < min) && !(v > max)
}

#[inline(always)]
unsafe fn step_cardinal_walk_frozen(
    flags: *const u32,
    size_x: i32,
    size_y: i32,
    max_cell: i32,
    idx: i32,
    dir: u8,
) -> i32 {
    // 0=East(+1), 1=West(-1), 2=North(-size_x), 3=South(+size_x)
    match dir {
        0 => {
            let n = idx + 1;
            if n % size_y == 0 || n < 0 || n > max_cell {
                return -1;
            }
            if (*flags.add(n as usize) & BIT_STEP_WALK_FROZEN_OK) != 0 { n } else { -1 }
        }
        1 => {
            let n = idx - 1;
            if n < 0 || n % size_y == size_y - 1 {
                return -1;
            }
            if (*flags.add(n as usize) & BIT_STEP_WALK_FROZEN_OK) != 0 { n } else { -1 }
        }
        2 => {
            let n = idx - size_x;
            if n < 0 {
                return -1;
            }
            if (*flags.add(n as usize) & BIT_STEP_WALK_FROZEN_OK) != 0 { n } else { -1 }
        }
        _ => {
            let n = idx + size_x;
            if n > max_cell {
                return -1;
            }
            if (*flags.add(n as usize) & BIT_STEP_WALK_FROZEN_OK) != 0 { n } else { -1 }
        }
    }
}

/// Evaluates PredationCheckFast for `locationType = Ground` on cell `base_idx`.
/// Returns: 1 = match, 0 = no match, -1 = needs C# fallback (prudent or multi-egg).
#[inline(always)]
unsafe fn check_predation_ground(g: &VisionGrid, q: &VisionQuery, base_idx: i32) -> i32 {
    let want_whole = (q.food_type_mask & FOOD_WHOLE_ANIMAL) != 0 && q.carn_species != -1;
    let want_blood = (q.food_type_mask & FOOD_BLOOD) != 0 && q.carn_species != -1;
    let want_egg = (q.food_type_mask & FOOD_EGG) != 0 && q.carn_dict_species != -1;
    let want_milk = (q.food_type_mask & FOOD_MILK) != 0;

    if !want_whole && !want_blood && !want_egg && !want_milk {
        return 0;
    }

    let flags = g.flags;
    let sx = g.size_x;
    let sy = g.size_y;
    let max_cell = sx * sy - 1;

    // 9 cells: self + 8 wind directions (exact StepFromCell(false, true) semantics).
    let n_n = step_cardinal_walk_frozen(flags, sx, sy, max_cell, base_idx, 2);
    let n_s = step_cardinal_walk_frozen(flags, sx, sy, max_cell, base_idx, 3);
    let n_e = step_cardinal_walk_frozen(flags, sx, sy, max_cell, base_idx, 0);
    let n_w = step_cardinal_walk_frozen(flags, sx, sy, max_cell, base_idx, 1);
    let n_nw = if n_n != -1 { step_cardinal_walk_frozen(flags, sx, sy, max_cell, n_n, 1) } else { -1 };
    let n_ne = if n_n != -1 { step_cardinal_walk_frozen(flags, sx, sy, max_cell, n_n, 0) } else { -1 };
    let n_sw = if n_s != -1 { step_cardinal_walk_frozen(flags, sx, sy, max_cell, n_s, 1) } else { -1 };
    let n_se = if n_s != -1 { step_cardinal_walk_frozen(flags, sx, sy, max_cell, n_s, 0) } else { -1 };

    let neighbors = [base_idx, n_n, n_ne, n_e, n_se, n_s, n_sw, n_w, n_nw];
    let need_bit = (if want_egg { BIT_HAS_EGG } else { 0 })
        | (if want_whole || want_blood || want_milk { BIT_ANIMAL_GROUND } else { 0 });

    let mut need_fallback = false;
    let carn_is_child = q.carn_is_child != 0;
    let prudent = q.prudent != 0;

    for &ni in neighbors.iter() {
        if ni == -1 {
            continue;
        }
        let nf = *flags.add(ni as usize);
        if (nf & BIT_NEST_ENCLOSED) != 0 || (nf & need_bit) == 0 {
            continue;
        }

        if want_egg && (nf & BIT_HAS_EGG) != 0 {
            let esp = *g.egg_species.add(ni as usize);
            if esp == -2 || prudent {
                need_fallback = true;
            } else if esp != -1 && esp != q.carn_dict_species {
                return 1;
            }
        }

        if (nf & BIT_ANIMAL_GROUND) != 0 {
            if want_milk && q.parent_lf_idx == *g.ga_lf_idx.add(ni as usize) {
                return 1;
            }
            if want_whole || want_blood {
                let psp = *g.ga_species.add(ni as usize);
                if psp == -1 || psp == q.carn_species || (nf & BIT_GA_HIDING_OR_CAMO) != 0 {
                    continue;
                }
                let pat = *g.ga_skin_pat.add(ni as usize) as u32;
                let in_range = in_range_inc(*g.ga_hue.add(ni as usize), q.w1_x, q.w1_y)
                    && in_range_inc(*g.ga_bright.add(ni as usize), q.w2_x, q.w2_y)
                    && in_range_inc(*g.ga_sec_hue.add(ni as usize), q.w3_x, q.w3_y)
                    && in_range_inc(*g.ga_sec_bright.add(ni as usize), q.w4_x, q.w4_y)
                    && (pat >= 32 || (q.exclude_skin_pats_mask & (1u32 << pat)) == 0);
                let ok = if q.invert_whole != 0 { !in_range } else { in_range };
                if !ok {
                    continue;
                }
                let prey_size = *g.ga_size.add(ni as usize);
                if want_blood && q.carn_size < prey_size {
                    return 1;
                }
                if want_whole {
                    let prey_is_child = (nf & BIT_GA_IS_CHILD) != 0;
                    // For Ground (locationType != UnderWater):
                    // - carnIsChild: carnSize - preySize > 1 || carnSize > preySize -> carnSize > preySize
                    // - !carnIsChild && !preyIsChild: carnSize > preySize
                    // - !carnIsChild && preyIsChild: always true
                    let can_eat_prey = if carn_is_child || !prey_is_child {
                        q.carn_size > prey_size
                    } else {
                        true
                    };
                    if can_eat_prey {
                        if prudent {
                            need_fallback = true;
                        } else {
                            return 1;
                        }
                    }
                }
            }
        }
    }

    if need_fallback { -1 } else { 0 }
}

/// Evaluates CellContainsFoodFast (with locationType = Ground) on `idx`.
/// Returns: 1 = match, 0 = no match, -1 = needs C# fallback (prudent or multi-egg).
#[inline(always)]
unsafe fn check_food_cell(g: &VisionGrid, q: &VisionQuery, idx: i32) -> i32 {
    let f = *g.flags.add(idx as usize);
    let fm = q.food_type_mask;

    // Fast direct-cell checks (ordered by cheapness; boolean existence is order-independent).
    if (f & q.direct_food_flags) != 0 {
        if (fm & FOOD_GRASS) != 0 && (f & BIT_GRASS) != 0 {
            let h = *g.grass_hue.add(idx as usize);
            let b = *g.grass_bright.add(idx as usize);
            let in_r = in_range_inc(h, q.r1_x, q.r1_y) && in_range_inc(b, q.r2_x, q.r2_y);
            if if q.invert_soft != 0 { !in_r } else { in_r } {
                return 1;
            }
        }
        if (fm & FOOD_SOFT_PLANT) != 0 {
            if (f & BIT_ALGA) != 0 {
                return 1;
            }
            if (f & BIT_PLANT) != 0 {
                let h = *g.plant_hue.add(idx as usize);
                let b = *g.plant_bright.add(idx as usize);
                let in_r = in_range_inc(h, q.r1_x, q.r1_y) && in_range_inc(b, q.r2_x, q.r2_y);
                if if q.invert_soft != 0 { !in_r } else { in_r } {
                    if (f & BIT_PLANT_BARK) == 0
                        || q.can_fly_or_climb != 0
                        || !(q.eat_height_x10 < *g.plant_leaf_h.add(idx as usize))
                    {
                        return 1;
                    }
                }
            }
        }
        if (fm & FOOD_PLANKTON) != 0 && (f & BIT_PLANKTON) != 0 {
            return 1;
        }
        if (fm & FOOD_MARINE_SNOW) != 0 && (f & (BIT_DEEP_SEA | BIT_PLANKTON)) == (BIT_DEEP_SEA | BIT_PLANKTON) {
            return 1;
        }
        if (fm & FOOD_SAP) != 0 && (f & BIT_PLANT) != 0 {
            return 1;
        }
        if (fm & FOOD_BARK) != 0 && (f & BIT_PLANT_BARK) != 0 {
            return 1;
        }
        if (fm & FOOD_NECTAR) != 0 && (f & BIT_PLANT_NECTAR) != 0 {
            return 1;
        }
        if (fm & FOOD_SEED) != 0 && (f & BIT_PLANT_SEED) != 0 && *g.plant_seed_h.add(idx as usize) <= q.eat_height_x10 {
            return 1;
        }
        if (fm & FOOD_NUT) != 0 && (f & BIT_PLANT_NUT) != 0 && *g.plant_nut_h.add(idx as usize) <= q.eat_height_x10 {
            return 1;
        }
        if (fm & FOOD_FRUIT) != 0
            && (f & BIT_PLANT_FRUIT) != 0
            && *g.plant_fruit_h.add(idx as usize) <= q.eat_height_x10
        {
            let fh = *g.plant_fruit_hue.add(idx as usize);
            let in_r = in_range_inc(fh, q.r3_x, q.r3_y);
            let shape = *g.plant_fruit_shape.add(idx as usize) as u32;
            if (if q.invert_fruit != 0 { !in_r } else { in_r })
                && (shape >= 32 || (q.exclude_fruit_shapes_mask & (1u32 << shape)) == 0)
            {
                return 1;
            }
        }
        if (fm & FOOD_FRESH_MEAT) != 0
            && (f & (BIT_CARRION | BIT_CARRION_OLD)) == BIT_CARRION
            && q.carn_species != -1
            && *g.carrion_species.add(idx as usize) != q.carn_species
        {
            return 1;
        }
        if (fm & FOOD_OLD_MEAT) != 0
            && (f & BIT_CARRION) != 0
            && q.carn_species != -1
            && *g.carrion_species.add(idx as usize) != q.carn_species
        {
            return 1;
        }
        if (fm & FOOD_FUNGUS) != 0 && (f & BIT_FUNGUS) != 0 {
            return 1;
        }
    }

    if (fm & (FOOD_WHOLE_ANIMAL | FOOD_EGG | FOOD_BLOOD | FOOD_MILK)) != 0 {
        return check_predation_ground(g, q, idx);
    }

    0
}

#[inline(always)]
unsafe fn eval_cell(g: &VisionGrid, q: &VisionQuery, idx: i32) -> i32 {
    match q.mode {
        MODE_FOOD => check_food_cell(g, q, idx),
        MODE_ANIMAL_GROUND => {
            let f = *g.flags.add(idx as usize);
            if (f & (BIT_ANIMAL_GROUND | BIT_NEST_ENCLOSED)) != BIT_ANIMAL_GROUND {
                return 0;
            }
            let pat = *g.ga_skin_pat.add(idx as usize) as u32;
            if pat >= 32 || (q.inst_skin_pat_mask & (1u32 << pat)) == 0 {
                return 0;
            }
            let h = *g.ga_hue.add(idx as usize);
            let b = *g.ga_bright.add(idx as usize);
            let sh = *g.ga_sec_hue.add(idx as usize);
            let sb = *g.ga_sec_bright.add(idx as usize);
            if h > q.r1_x && h < q.r1_y
                && b > q.r2_x && b < q.r2_y
                && sh > q.r3_x && sh < q.r3_y
                && sb > q.r4_x && sb < q.r4_y
            {
                1
            } else {
                0
            }
        }
        MODE_COLOR_GROUND => {
            let f = *g.flags.add(idx as usize);
            if (f & (BIT_ANIMAL_GROUND | BIT_NEST_ENCLOSED)) != BIT_ANIMAL_GROUND {
                return 0;
            }
            let h = *g.ga_salient_hue.add(idx as usize);
            let b = *g.ga_salient_bright.add(idx as usize);
            if !(h <= q.r1_x) && !(h >= q.r1_y) && !(b <= q.r2_x) && !(b >= q.r2_y) {
                1
            } else {
                0
            }
        }
        MODE_PLANT => {
            let f = *g.flags.add(idx as usize);
            if (f & BIT_PLANT) == 0 {
                return 0;
            }
            let h = *g.plant_hue.add(idx as usize);
            let b = *g.plant_bright.add(idx as usize);
            if !(h > q.r1_x && h < q.r1_y && b > q.r2_x && b < q.r2_y) {
                return 0;
            }
            let fh = *g.plant_fruit_hue.add(idx as usize);
            let flh = *g.plant_flower_hue.add(idx as usize);
            if (fh > q.r3_x && fh < q.r3_y) || (flh > q.r3_x && flh < q.r3_y) {
                1
            } else {
                0
            }
        }
        MODE_ALGA => {
            let f = *g.flags.add(idx as usize);
            if (f & (BIT_ALGA | BIT_PLANKTON)) == 0 {
                return 0;
            }
            let h = *g.alga_hue.add(idx as usize);
            let b = *g.alga_bright.add(idx as usize);
            if h > q.r1_x && h < q.r1_y && b > q.r2_x && b < q.r2_y {
                1
            } else {
                0
            }
        }
        MODE_FIRST_FLAG => {
            let f = *g.flags.add(idx as usize);
            if (f & q.flag_req_mask) == q.flag_req_expected { 1 } else { 0 }
        }
        _ => 0,
    }
}

/// Scans `BAND[d]` for `d = start_d ..= max_d` in exact retail order.
///
/// Return value:
/// - `>= 0`: matched cell index (for MODE_FIRST_FLAG, `out_next_d`/`out_next_slot` are set so caller can resume if rejected)
/// - `-1`: no match found up to `max_d`
/// - `<= -2`: cell `-(ret + 2)` requires C# fallback verification; `out_next_d`/`out_next_slot` point to the slot immediately after it
pub unsafe fn vision_search(
    g: &VisionGrid,
    q: &VisionQuery,
    out_next_d: *mut i32,
    out_next_slot: *mut i32,
) -> i32 {
    if g.flags.is_null() || g.size_x <= 0 || g.size_y <= 0 || g.cell_count <= 0 {
        return -1;
    }

    let sx = g.size_x;
    let sy = g.size_y;
    let cx = q.center_x;
    let cy = q.center_y;

    let top = (q.dir_mask & FLAG_TOP) != 0;
    let left = (q.dir_mask & FLAG_LEFT) != 0;
    let right = (q.dir_mask & FLAG_RIGHT) != 0;
    let bottom = (q.dir_mask & FLAG_BOTTOM) != 0;

    let d_lo = if q.start_d < 1 { 1 } else { q.start_d };
    let d_hi = if q.max_d > MAX_DISTANCE { MAX_DISTANCE } else { q.max_d };
    if d_lo > d_hi {
        return -1;
    }

    macro_rules! try_slot {
        ($d:expr, $slot:expr, $min_slot:expr, $nx:expr, $ny:expr) => {{
            if $slot >= $min_slot {
                let idx = $nx * sy + $ny;
                let res = eval_cell(g, q, idx);
                if res != 0 {
                    if !out_next_d.is_null() {
                        *out_next_d = $d;
                    }
                    if !out_next_slot.is_null() {
                        *out_next_slot = $slot + 1;
                    }
                    return if res > 0 { idx } else { -(idx + 2) };
                }
            }
        }};
    }

    for d in d_lo..=d_hi {
        let min_slot = if d == q.start_d { q.start_slot } else { 0 };
        let pairs = BAND[d as usize];
        for (pi, &(xi, yi, _)) in pairs.iter().enumerate() {
            let base_slot = (pi as i32) * 8;
            if base_slot + 7 < min_slot {
                continue;
            }
            let x = xi as i32;
            let y = yi as i32;
            let diagonal = x == y;

            let cx_px = cx + x;
            let cx_mx = cx - x;
            let cy_py = cy + y;
            let cy_my = cy - y;

            // slot 0: (+x, +y)
            if cx_px < sx && cy_py < sy && (top || (right && diagonal)) {
                try_slot!(d, base_slot, min_slot, cx_px, cy_py);
            }
            // slot 1: (-x, -y)
            if cx_mx >= 0 && cy_my >= 0 && (bottom || (left && diagonal)) {
                try_slot!(d, base_slot + 1, min_slot, cx_mx, cy_my);
            }
            if x != 0 && y != 0 {
                // slot 2: (+x, -y)
                if cx_px < sx && cy_my >= 0 && (bottom || (right && diagonal)) {
                    try_slot!(d, base_slot + 2, min_slot, cx_px, cy_my);
                }
                // slot 3: (-x, +y)
                if cx_mx >= 0 && cy_py < sy && (top || (left && diagonal)) {
                    try_slot!(d, base_slot + 3, min_slot, cx_mx, cy_py);
                }
            }
            if !diagonal {
                let cx_py = cx + y;
                let cx_my = cx - y;
                let cy_px = cy + x;
                let cy_mx = cy - x;

                // slot 4: (+y, +x)
                if cx_py < sx && cy_px < sy && right {
                    try_slot!(d, base_slot + 4, min_slot, cx_py, cy_px);
                }
                // slot 5: (-y, -x)
                if cx_my >= 0 && cy_mx >= 0 && left {
                    try_slot!(d, base_slot + 5, min_slot, cx_my, cy_mx);
                }
                if x != 0 && y != 0 {
                    // slot 6: (+y, -x)
                    if cx_py < sx && cy_mx >= 0 && right {
                        try_slot!(d, base_slot + 6, min_slot, cx_py, cy_mx);
                    }
                    // slot 7: (-y, +x)
                    if cx_my >= 0 && cy_px < sy && left {
                        try_slot!(d, base_slot + 7, min_slot, cx_my, cy_px);
                    }
                }
            }
        }
    }

    -1
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::band::fill_band;

    #[test]
    fn test_vision_order_matches_band_fill() {
        let sx = 32;
        let sy = 32;
        let n = (sx * sy) as usize;
        let mut flags = vec![0u32; n];
        // Mark every cell as having grass so MODE_FIRST_FLAG enumerates every visited cell.
        for f in flags.iter_mut() {
            *f = BIT_GRASS;
        }
        let grid = VisionGrid {
            size_x: sx,
            size_y: sy,
            cell_count: n as i32,
            flags: flags.as_ptr(),
            plant_hue: std::ptr::null(),
            plant_bright: std::ptr::null(),
            plant_fruit_hue: std::ptr::null(),
            plant_flower_hue: std::ptr::null(),
            plant_leaf_h: std::ptr::null(),
            plant_seed_h: std::ptr::null(),
            plant_nut_h: std::ptr::null(),
            plant_fruit_h: std::ptr::null(),
            plant_fruit_shape: std::ptr::null(),
            grass_hue: std::ptr::null(),
            grass_bright: std::ptr::null(),
            alga_hue: std::ptr::null(),
            alga_bright: std::ptr::null(),
            carrion_species: std::ptr::null(),
            egg_species: std::ptr::null(),
            ga_hue: std::ptr::null(),
            ga_bright: std::ptr::null(),
            ga_sec_hue: std::ptr::null(),
            ga_sec_bright: std::ptr::null(),
            ga_salient_hue: std::ptr::null(),
            ga_salient_bright: std::ptr::null(),
            ga_skin_pat: std::ptr::null(),
            ga_species: std::ptr::null(),
            ga_size: std::ptr::null(),
            ga_lf_idx: std::ptr::null(),
        };

        let cx = 10;
        let cy = 15;
        let center = cx * sy + cy;
        for mask in [0x0Fu8, 0x01, 0x05, 0x0A, 0x07] {
            let mut expected = Vec::new();
            let mut buf = [0i32; 256];
            for d in 1..=6 {
                let w = fill_band(sx as f32, sy as f32, center, d, mask, &mut buf);
                for &v in &buf[..w as usize] {
                    if v != -1 {
                        expected.push(v);
                    }
                }
            }

            let mut got = Vec::new();
            let mut q = VisionQuery {
                mode: MODE_FIRST_FLAG,
                center_x: cx,
                center_y: cy,
                start_d: 1,
                start_slot: 0,
                max_d: 6,
                dir_mask: mask,
                invert_soft: 0,
                invert_fruit: 0,
                invert_whole: 0,
                can_fly_or_climb: 0,
                carn_is_child: 0,
                prudent: 0,
                _pad0: 0,
                food_type_mask: 0,
                direct_food_flags: 0,
                exclude_fruit_shapes_mask: 0,
                exclude_skin_pats_mask: 0,
                inst_skin_pat_mask: 0,
                flag_req_mask: BIT_GRASS,
                flag_req_expected: BIT_GRASS,
                eat_height_x10: 0.0,
                carn_species: -1,
                carn_dict_species: -1,
                carn_size: 10,
                parent_lf_idx: -1,
                r1_x: 0.0, r1_y: 0.0, r2_x: 0.0, r2_y: 0.0,
                r3_x: 0.0, r3_y: 0.0, r4_x: 0.0, r4_y: 0.0,
                w1_x: 0.0, w1_y: 0.0, w2_x: 0.0, w2_y: 0.0,
                w3_x: 0.0, w3_y: 0.0, w4_x: 0.0, w4_y: 0.0,
            };
            loop {
                let mut nd = 0;
                let mut ns = 0;
                let hit = unsafe { vision_search(&grid, &q, &mut nd, &mut ns) };
                if hit < 0 {
                    break;
                }
                got.push(hit);
                q.start_d = nd;
                q.start_slot = ns;
            }
            assert_eq!(got, expected, "mismatch for mask {mask}");
        }
    }
}
