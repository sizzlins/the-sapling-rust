//! Ecosystem simulation core - engine-free, dependency-free.
//!
//! Exposes a pure Rust core and a C-ABI FFI layer for native host embedding
//! (e.g. Unity P/Invoke via `sapling_rust.dll`).

pub mod ffi;
pub mod sim;

pub use sim::{Herbivores, Plants, Rng, World};