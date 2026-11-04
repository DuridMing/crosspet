//! Redrawing only the changed regions (MC-39). Layers are cached; only dirty rects are redrawn and sent out,
//! which is what `prcDirty` of `UpdateLayeredWindowIndirect` does.
//!     cargo run --release --bin damage -- [sprite dir] [frames]
use composite::*;
use std::hint::black_box;
use std::time::Instant;

const TILE: usize = 216;

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let dir = args.get(1).map(std::path::PathBuf::from).unwrap_or_else(default_sprite_dir);
    let frames: usize = args.get(2).and_then(|s| s.parse().ok()).unwrap_or(600);

    let idle = decode_webp(&dir.join("idle.webp"));
    let draw_w = idle.w * SPRITE_DRAW_H / idle.h;
    let sprite = scale_bilinear(&idle, draw_w, SPRITE_DRAW_H);
    let x = (CANVAS_W - draw_w) / 2;
    let tiles = [effect_tile(TILE, [80, 160, 240]), effect_tile(TILE, [230, 200, 90]), effect_tile(TILE, [120, 220, 140])];
    let sprite_rect = |y: usize| Rect { x, y, w: draw_w, h: SPRITE_DRAW_H };
    let tile_rect = |i: usize, f: usize| Rect {
        x: 60 + i * 420 + (f % 40),
        y: 60 + (f % 30),
        w: TILE,
        h: TILE,
    };
    let mut canvas = Bitmap::new(CANVAS_W, CANVAS_H);
    let mut out = Bitmap::new(CANVAS_W, CANVAS_H);

    // Idle, bob drawn inside the window: the sprite region changes every frame.
    let mut y_prev = 100 + bob_offset(0);
    let whole = canvas.rect();
    blend(&mut canvas, &sprite, x, y_prev, whole);
    let (t0, mut pixels) = (Instant::now(), 0usize);
    for f in 1..=frames {
        let y = 100 + bob_offset(f);
        let dirty = sprite_rect(y_prev).union(sprite_rect(y));
        clear(&mut canvas, dirty);
        blend(&mut canvas, &sprite, x, y, dirty);
        present(&mut out, &canvas, dirty);
        black_box(&out);
        pixels += dirty.pixels();
        y_prev = y;
    }
    report("idle_bob_redraw", frames, t0.elapsed(), pixels as f64 / frames as f64);

    // Idle, bob done by moving the window: nothing is redrawn.
    let t0 = Instant::now();
    for _ in 0..frames {
        black_box(&out);
    }
    report("idle_bob_move_window", frames, t0.elapsed(), 0.0);

    // Working, bob drawn inside the window, plus three effect tiles.
    let (t0, mut pixels) = (Instant::now(), 0usize);
    for f in 1..=frames {
        let y = 100 + bob_offset(f);
        let mut dirty = sprite_rect(y_prev).union(sprite_rect(y));
        for i in 0..tiles.len() {
            dirty = dirty.union(tile_rect(i, f - 1)).union(tile_rect(i, f));
        }
        clear(&mut canvas, dirty);
        blend(&mut canvas, &sprite, x, y, dirty);
        for (i, tile) in tiles.iter().enumerate() {
            let r = tile_rect(i, f);
            blend(&mut canvas, tile, r.x, r.y, dirty);
        }
        present(&mut out, &canvas, dirty);
        black_box(&out);
        pixels += dirty.pixels();
        y_prev = y;
    }
    report("work_bob_redraw_effects", frames, t0.elapsed(), pixels as f64 / frames as f64);

    // Working, bob done by moving the window: only the effect tiles are redrawn.
    let y = y_prev;
    let (t0, mut pixels) = (Instant::now(), 0usize);
    for f in 1..=frames {
        for (i, tile) in tiles.iter().enumerate() {
            let dirty = tile_rect(i, f - 1).union(tile_rect(i, f));
            clear(&mut canvas, dirty);
            blend(&mut canvas, &sprite, x, y, dirty);
            let r = tile_rect(i, f);
            blend(&mut canvas, tile, r.x, r.y, dirty);
            present(&mut out, &canvas, dirty);
            pixels += dirty.pixels();
        }
        black_box(&out);
    }
    report("work_effects_move_window", frames, t0.elapsed(), pixels as f64 / frames as f64);
}
