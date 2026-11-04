//! Sprite decoding and full-frame redraw (MC-39). Shows why redrawing the whole window every frame is too costly.
//!     cargo run --release --bin native -- [sprite dir] [frames]
use composite::*;
use std::hint::black_box;
use std::time::Instant;

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let dir = args.get(1).map(std::path::PathBuf::from).unwrap_or_else(default_sprite_dir);
    let frames: usize = args.get(2).and_then(|s| s.parse().ok()).unwrap_or(300);

    let (sprites, took) = load_sprites(&dir);
    let bytes: usize = sprites.iter().map(|(_, b)| b.px.len()).sum();
    println!(
        "{{\"scenario\":\"decode_all\",\"sprites\":{},\"decoded_mb\":{:.1},\"total_ms\":{},\"ms_per_sprite\":{:.1}}}",
        sprites.len(),
        bytes as f64 / 1e6,
        took.as_millis(),
        took.as_secs_f64() * 1000.0 / sprites.len().max(1) as f64
    );
    let idle = &sprites.iter().find(|(n, _)| n == "idle").expect("idle sprite").1;
    let draw_w = idle.w * SPRITE_DRAW_H / idle.h;
    let canvas_rect = Rect { x: 0, y: 0, w: CANVAS_W, h: CANVAS_H };
    let mut canvas = Bitmap::new(CANVAS_W, CANVAS_H);
    let mut out = Bitmap::new(CANVAS_W, CANVAS_H);
    let x = (CANVAS_W - draw_w) / 2;

    let t0 = Instant::now();
    for f in 0..frames {
        clear(&mut canvas, canvas_rect);
        let scaled = scale_bilinear(idle, draw_w, SPRITE_DRAW_H);
        blend(&mut canvas, &scaled, x, 100 + bob_offset(f), canvas_rect);
        present(&mut out, &canvas, canvas_rect);
        black_box(&out);
    }
    report("full_redraw_scale_every_frame", frames, t0.elapsed(), canvas_rect.pixels() as f64);

    let scaled = scale_bilinear(idle, draw_w, SPRITE_DRAW_H);
    let t0 = Instant::now();
    for f in 0..frames {
        clear(&mut canvas, canvas_rect);
        blend(&mut canvas, &scaled, x, 100 + bob_offset(f), canvas_rect);
        present(&mut out, &canvas, canvas_rect);
        black_box(&out);
    }
    report("full_redraw_cached_scale", frames, t0.elapsed(), canvas_rect.pixels() as f64);
}
