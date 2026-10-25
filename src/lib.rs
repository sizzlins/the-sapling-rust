//! Ecosystem simulation core - engine-free, dependency-free.
//!
//! Rendering/wiring (Bevy) is deliberately not in this crate: complexity claims have to
//! be testable without a window, and a 5-minute Bevy compile on every `cargo test` would
//! make the tests the thing you skip.

pub mod sim;

pub use sim::{Herbivores, Plants, Rng, World};