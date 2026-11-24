//! Exact port of `CellDistanceFinder.GetAllCellsWithinDistanceBandOfCellWithCoordinates`
//! (Assembly-CSharp-firstpass, retail Unity 2017.4.40f1).
//!
//! The retail "band" is neither a ring nor a disc. It is a fixed, hand-authored
//! list of half-quadrant offsets; each offset is mirrored into 8 slots, and slots
//! that fall out of bounds or are gated off by a direction flag are written as
//! `-1`. Callers iterate the array in order and skip the holes.
//!
//! The offset table is transcribed verbatim from the decompiled switch
//! (cases 1..=20) and must never be "cleaned up": it is irregular on purpose
//! (d=12 has 9 offset pairs, d=13 has 12) because it was authored by hand.
//! Any mathematically neater reconstruction changes behaviour, which is exactly
//! how the previous native attempt broke animal food instincts.
//!
//! Correctness here is established by differential test against the retail
//! assembly, not by inspection: see `tools/parity-harness`.

/// Largest `distance` the retail switch handles. Any other value returns an
/// empty array (`new int[0]`), not a clamp.
pub const MAX_DISTANCE: i32 = 20;

/// `(x, y, pointer)` per distance. `pointer` selects one of up to 16
/// eight-slot blocks, so the output is `8 * pairs` ints long.
static BAND: [&[(u8, u8, u8)]; 21] = [
    &[], // 0 - no case; caller gets an empty band
    &[(0, 1, 0)], // 1
    &[(0, 2, 0), (1, 1, 1)], // 2
    &[(0, 3, 0), (1, 2, 1), (2, 2, 2)], // 3
    &[(0, 4, 0), (1, 3, 1), (2, 3, 2)], // 4
    &[(0, 5, 0), (1, 4, 1), (2, 4, 2), (3, 3, 3), (3, 4, 4)], // 5
    &[(0, 6, 0), (1, 5, 1), (2, 5, 2), (3, 5, 3), (4, 4, 4)], // 6
    &[(0, 7, 0), (1, 6, 1), (2, 6, 2), (3, 6, 3), (4, 5, 4)], // 7
    &[(0, 8, 0), (1, 7, 1), (2, 7, 2), (3, 7, 3), (4, 6, 4), (5, 5, 5), (5, 6, 6)], // 8
    &[(0, 9, 0), (1, 8, 1), (2, 8, 2), (3, 8, 3), (4, 7, 4), (4, 8, 5), (5, 7, 6), (6, 6, 7)], // 9
    &[(0, 10, 0), (1, 9, 1), (2, 9, 2), (3, 9, 3), (4, 9, 4), (5, 8, 5), (6, 7, 6), (6, 8, 7), (7, 7, 8)], // 10
    &[(0, 11, 0), (1, 10, 1), (2, 10, 2), (3, 10, 3), (4, 10, 4), (5, 9, 5), (6, 9, 6), (7, 8, 7)], // 11
    &[(0, 12, 0), (1, 11, 1), (2, 11, 2), (3, 11, 3), (4, 11, 4), (5, 10, 5), (6, 10, 6), (7, 9, 7), (8, 8, 8)], // 12
    &[(0, 13, 0), (1, 12, 1), (2, 12, 2), (3, 12, 3), (4, 12, 4), (5, 11, 5), (5, 12, 6), (6, 11, 7), (7, 10, 8), (8, 9, 9), (8, 10, 10), (9, 9, 11)], // 13
    &[(0, 14, 0), (1, 14, 1), (2, 14, 2), (3, 14, 3), (4, 14, 4), (5, 14, 5), (6, 13, 6), (7, 13, 7), (8, 12, 8), (9, 11, 9), (10, 10, 10), (10, 11, 11)], // 14
    &[(0, 15, 0), (1, 15, 1), (2, 15, 2), (3, 15, 3), (4, 15, 4), (5, 15, 5), (6, 14, 6), (7, 14, 7), (8, 13, 8), (9, 12, 9), (9, 13, 10), (10, 12, 11), (11, 11, 12)], // 15
    &[(0, 16, 0), (1, 16, 1), (2, 16, 2), (3, 16, 3), (4, 16, 4), (5, 16, 5), (6, 15, 6), (7, 15, 7), (8, 14, 8), (9, 14, 9), (10, 13, 10), (11, 12, 11), (12, 12, 12)], // 16
    &[(0, 17, 0), (1, 17, 1), (2, 17, 2), (3, 17, 3), (4, 17, 4), (5, 17, 5), (6, 16, 6), (7, 16, 7), (8, 15, 8), (8, 16, 9), (9, 15, 10), (10, 14, 11), (11, 13, 12), (11, 14, 13), (12, 13, 14)], // 17
    &[(0, 18, 0), (1, 18, 1), (2, 18, 2), (3, 18, 3), (4, 18, 4), (5, 18, 5), (6, 17, 6), (6, 18, 7), (7, 17, 8), (8, 17, 9), (9, 16, 10), (10, 15, 11), (10, 16, 12), (11, 15, 13), (12, 14, 14), (13, 13, 15)], // 18
    &[(0, 19, 0), (1, 19, 1), (2, 19, 2), (3, 19, 3), (4, 19, 4), (5, 19, 5), (6, 19, 6), (7, 18, 7), (8, 18, 8), (9, 17, 9), (10, 17, 10), (11, 16, 11), (12, 15, 12), (13, 14, 13), (13, 15, 14), (14, 14, 15)], // 19
    &[(0, 20, 0), (1, 20, 1), (2, 20, 2), (3, 20, 3), (4, 20, 4), (5, 20, 5), (6, 20, 6), (7, 19, 7), (8, 19, 8), (9, 18, 9), (10, 18, 10), (11, 17, 11), (12, 16, 12), (12, 17, 13), (13, 16, 14), (14, 15, 15)], // 20
];

/// Direction bits, matching the order of the retail parameters.
pub const FLAG_TOP: u8 = 1;
pub const FLAG_LEFT: u8 = 2;
pub const FLAG_RIGHT: u8 = 4;
pub const FLAG_BOTTOM: u8 = 8;

/// Length of the array retail would allocate for `distance`, or 0 when the
/// switch has no case for it.
pub fn band_len(distance: i32) -> usize {
    if distance <= 0 || distance > MAX_DISTANCE {
        return 0;
    }
    BAND[distance as usize].len() * 8
}

#[inline]
fn cell_index(size_y: i32, x: i32, y: i32) -> i32 {
    x * size_y + y
}

/// Write the retail band for `center` into `out`, returning the number of ints
/// written. Mirrors `AddCoordinatesAtDistanceToList` slot for slot, including
/// the `-1` holes and the order in which they appear.
///
/// `size_x`/`size_y` are floats on purpose: retail bounds-checks against the
/// float size (`(float)(centerX + x) < size.x`) while the cell stride uses the
/// truncated `(int)size.y`, so fractional area sizes are not interchangeable.
pub fn fill_band(
    size_x: f32,
    size_y: f32,
    center: i32,
    distance: i32,
    mask: u8,
    out: &mut [i32],
) -> i32 {
    let needed = band_len(distance);
    if needed == 0 {
        return 0;
    }
    if out.len() < needed {
        return -(needed as i32);
    }

    let size_y_int = size_y as i32;
    if size_y_int <= 0 || size_x <= 0.0 || size_x.is_nan() {
        return 0;
    }

    let center_x = (center as f32 / size_y).floor() as i32;
    let center_y = center % size_y_int;

    let top = mask & FLAG_TOP != 0;
    let left = mask & FLAG_LEFT != 0;
    let right = mask & FLAG_RIGHT != 0;
    let bottom = mask & FLAG_BOTTOM != 0;

    // Deliberate defect for the harness' mutation probe: a build with
    // `--features mutate` swaps the left/right gates, which must be detected.
    #[cfg(feature = "mutate")]
    let (left, right) = (right, left);

    for &(xi, yi, pointer) in BAND[distance as usize].iter() {
        let x = xi as i32;
        let y = yi as i32;
        let b = pointer as usize * 8;
        let diagonal = x == y;

        // slot 0: (+x, +y)
        out[b] = if ((center_x + x) as f32) < size_x
            && ((center_y + y) as f32) < size_y
            && (top || (right && diagonal))
        {
            cell_index(size_y_int, center_x + x, center_y + y)
        } else {
            -1
        };

        // slot 1: (-x, -y)
        out[b + 1] = if center_x - x >= 0
            && center_y - y >= 0
            && (bottom || (left && diagonal))
        {
            cell_index(size_y_int, center_x - x, center_y - y)
        } else {
            -1
        };

        if x != 0 && y != 0 {
            // slot 2: (+x, -y)
            out[b + 2] = if ((center_x + x) as f32) < size_x
                && center_y - y >= 0
                && (bottom || (right && diagonal))
            {
                cell_index(size_y_int, center_x + x, center_y - y)
            } else {
                -1
            };
            // slot 3: (-x, +y)
            out[b + 3] = if center_x - x >= 0
                && ((center_y + y) as f32) < size_y
                && (top || (left && diagonal))
            {
                cell_index(size_y_int, center_x - x, center_y + y)
            } else {
                -1
            };
        } else {
            out[b + 2] = -1;
            out[b + 3] = -1;
        }

        if x != y {
            // slot 4: (+y, +x)
            out[b + 4] = if ((center_x + y) as f32) < size_x
                && ((center_y + x) as f32) < size_y
                && right
            {
                cell_index(size_y_int, center_x + y, center_y + x)
            } else {
                -1
            };
            // slot 5: (-y, -x)
            out[b + 5] = if center_x - y >= 0 && center_y - x >= 0 && left {
                cell_index(size_y_int, center_x - y, center_y - x)
            } else {
                -1
            };
            if x != 0 && y != 0 {
                // slot 6: (+y, -x)
                out[b + 6] = if ((center_x + y) as f32) < size_x && center_y - x >= 0 && right {
                    cell_index(size_y_int, center_x + y, center_y - x)
                } else {
                    -1
                };
                // slot 7: (-y, +x)
                out[b + 7] = if center_x - y >= 0 && ((center_y + x) as f32) < size_y && left {
                    cell_index(size_y_int, center_x - y, center_y + x)
                } else {
                    -1
                };
            } else {
                out[b + 6] = -1;
                out[b + 7] = -1;
            }
        } else {
            out[b + 4] = -1;
            out[b + 5] = -1;
            out[b + 6] = -1;
            out[b + 7] = -1;
        }
    }

    needed as i32
}

/// Pointer-taking wrapper for the C ABI. Returns the number of ints written,
/// `-(required)` when `out` is too small, or 0 when the distance has no case.
///
/// # Safety
/// `out` must be valid for writes of `out_cap` ints when `out_cap >= required`.
pub unsafe fn ffi_fill(
    size_x: f32,
    size_y: f32,
    center: i32,
    distance: i32,
    mask: u8,
    out: *mut i32,
    out_cap: i32,
) -> i32 {
    let needed = band_len(distance);
    if needed == 0 {
        return 0;
    }
    if out.is_null() || out_cap < 0 || (out_cap as usize) < needed {
        return -(needed as i32);
    }
    let buf = std::slice::from_raw_parts_mut(out, needed);
    fill_band(size_x, size_y, center, distance, mask, buf)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn lengths_match_the_retail_table() {
        assert_eq!(band_len(0), 0);
        assert_eq!(band_len(21), 0);
        assert_eq!(band_len(-3), 0);
        assert_eq!(band_len(1), 8);
        assert_eq!(band_len(2), 16);
        assert_eq!(band_len(13), 96);
        assert_eq!(band_len(20), 128);
    }

    #[test]
    fn pointer_blocks_do_not_overlap() {
        for d in 1..=MAX_DISTANCE {
            let mut seen = [false; 16];
            for &(_, _, p) in BAND[d as usize].iter() {
                assert!(!seen[p as usize], "distance {d} reuses pointer {p}");
                seen[p as usize] = true;
            }
        }
    }

    #[test]
    fn center_cell_is_never_emitted() {
        let mut out = vec![0i32; 128];
        for d in 1..=MAX_DISTANCE {
            let n = fill_band(64.0, 64.0, 32 * 64 + 32, d, 0b1111, &mut out);
            assert_eq!(n as usize, band_len(d));
            assert!(!out[..n as usize].contains(&(32 * 64 + 32)));
        }
    }

    #[test]
    fn too_small_buffer_reports_required_length() {
        let mut out = vec![0i32; 4];
        assert_eq!(fill_band(64.0, 64.0, 0, 5, 0b1111, &mut out), -40);
    }

    #[test]
    fn corner_writes_holes_not_out_of_bounds_indices() {
        let mut out = vec![0i32; 128];
        let n = fill_band(64.0, 64.0, 0, 1, FLAG_TOP | FLAG_RIGHT, &mut out) as usize;
        assert_eq!(n, 8);
        // Pinned to the retail oracle (top+right from cell 0 of a 64x64 world),
        // not to whatever this implementation happens to produce.
        assert_eq!(&out[..n], &[1, -1, -1, -1, 64, -1, -1, -1]);
        assert_eq!(out[..n].iter().filter(|&&v| v == -1).count(), 6);
    }
}
