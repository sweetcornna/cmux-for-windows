// Instanced rounded rectangles in physical pixel space.
//
// Cell backgrounds, sidebar rows, tab chrome, and notification badges are all
// the same primitive: a rect with an optional corner radius. Terminal cells
// pass radius 0, which reduces to a plain quad.

struct Uniforms {
    resolution: vec2<f32>,
    _pad: vec2<f32>,
};

@group(0) @binding(0) var<uniform> u: Uniforms;

struct Instance {
    @location(0) rect: vec4<f32>,
    @location(1) color: vec4<f32>,
    @location(2) radius: f32,
};

struct VsOut {
    @builtin(position) pos: vec4<f32>,
    @location(0) color: vec4<f32>,
    // Fragment position relative to the rect's center, for the corner SDF.
    @location(1) local: vec2<f32>,
    @location(2) half_size: vec2<f32>,
    @location(3) radius: f32,
};

@vertex
fn vs_main(@builtin(vertex_index) vertex_index: u32, inst: Instance) -> VsOut {
    var corners = array<vec2<f32>, 6>(
        vec2<f32>(0.0, 0.0),
        vec2<f32>(1.0, 0.0),
        vec2<f32>(0.0, 1.0),
        vec2<f32>(1.0, 0.0),
        vec2<f32>(1.0, 1.0),
        vec2<f32>(0.0, 1.0),
    );
    let corner = corners[vertex_index];
    let pixel = inst.rect.xy + corner * inst.rect.zw;

    // Pixel space has origin top-left; NDC is bottom-left, hence the y flip.
    let ndc = vec2<f32>(
        pixel.x / u.resolution.x * 2.0 - 1.0,
        1.0 - pixel.y / u.resolution.y * 2.0,
    );

    let half_size = inst.rect.zw * 0.5;

    var out: VsOut;
    out.pos = vec4<f32>(ndc, 0.0, 1.0);
    out.color = inst.color;
    out.local = (corner - vec2<f32>(0.5, 0.5)) * inst.rect.zw;
    out.half_size = half_size;
    out.radius = min(inst.radius, min(half_size.x, half_size.y));
    return out;
}

// Signed distance to a rounded box, positive outside.
fn rounded_box_sdf(local: vec2<f32>, half_size: vec2<f32>, radius: f32) -> f32 {
    let q = abs(local) - half_size + vec2<f32>(radius, radius);
    return length(max(q, vec2<f32>(0.0, 0.0))) + min(max(q.x, q.y), 0.0) - radius;
}

@fragment
fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
    if (in.radius <= 0.0) {
        return in.color;
    }
    let dist = rounded_box_sdf(in.local, in.half_size, in.radius);
    // One-pixel analytic edge so corners are not stair-stepped.
    let alpha = 1.0 - smoothstep(-0.5, 0.5, dist);
    return vec4<f32>(in.color.rgb, in.color.a * alpha);
}
