//! C-ABI Foreign Function Interface for embedding in Unity C# via P/Invoke.
//!
//! Only exports functions proven byte-identical against the retail assembly and
//! actively called by `SaplingRustBridge.dll`.

use std::panic::catch_unwind;

/// Retail `CellDistanceFinder.GetAllCellsWithinDistanceBandOfCellWithCoordinates`.
///
/// `mask` bits: 1 = top, 2 = left, 4 = right, 8 = bottom.
/// Returns the number of ints written into `out`, or `-(required)` when `out` is
/// too small, or 0 when retail's switch has no case for `distance` (which is
/// every value outside 1..=20 — retail returns `new int[0]`, it does not clamp).
///
/// # Safety
/// `out` must be valid for writes of `out_cap` ints when `out_cap >= required`.
#[no_mangle]
pub unsafe extern "C" fn sapling_band_fill(
    size_x: f32,
    size_y: f32,
    center_index: i32,
    distance: i32,
    mask: u8,
    out: *mut i32,
    out_cap: i32,
) -> i32 {
    catch_unwind(|| {
        crate::band::ffi_fill(size_x, size_y, center_index, distance, mask, out, out_cap)
    })
    .unwrap_or(-1)
}

/// Native L1/L2-cache instinct & food ring scanner over a pinned SoA WorldArea snapshot.
///
/// # Safety
/// `grid` and `query` must point to valid structs whose array pointers remain valid
/// for the duration of the call. `out_next_d` and `out_next_slot` may be null.
#[no_mangle]
pub unsafe extern "C" fn sapling_vision_search(
    grid: *const crate::vision::VisionGrid,
    query: *const crate::vision::VisionQuery,
    out_next_d: *mut i32,
    out_next_slot: *mut i32,
) -> i32 {
    if grid.is_null() || query.is_null() {
        return -1;
    }
    catch_unwind(|| crate::vision::vision_search(&*grid, &*query, out_next_d, out_next_slot))
        .unwrap_or(-1)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::band::{FLAG_RIGHT, FLAG_TOP};

    #[test]
    fn test_c_abi_band_fill() {
        let mut out = [0i32; 16];
        let n = unsafe {
            sapling_band_fill(
                64.0,
                64.0,
                0,
                1,
                FLAG_TOP | FLAG_RIGHT,
                out.as_mut_ptr(),
                out.len() as i32,
            )
        };
        assert_eq!(n, 8);
        assert_eq!(&out[..8], &[1, -1, -1, -1, 64, -1, -1, -1]);
    }

    #[test]
    fn test_c_abi_band_fill_rejects_null_and_short_buf() {
        let mut small = [0i32; 4];
        unsafe {
            assert_eq!(
                sapling_band_fill(64.0, 64.0, 0, 1, 0b1111, std::ptr::null_mut(), 16),
                -8
            );
            assert_eq!(
                sapling_band_fill(64.0, 64.0, 0, 5, 0b1111, small.as_mut_ptr(), 4),
                -40
            );
            assert_eq!(
                sapling_band_fill(64.0, 64.0, 0, 21, 0b1111, small.as_mut_ptr(), 4),
                0
            );
        }
    }
}
