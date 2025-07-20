/**
 * DatumIngest™ Hero Animation
 *
 * Three-layer canvas animation:
 *   1. Stars / constellation network — parallax on mouse
 *   2. Volumetric clouds — Perlin-noise driven, mouse-reactive
 *   3. Lightning bolts — periodic branching flashes
 *
 * Respects prefers-reduced-motion: falls back to a static starfield.
 */
(function () {
  "use strict";

  const canvas = document.getElementById("hero-canvas");
  if (!canvas) return;
  const ctx = canvas.getContext("2d");

  const prefersReducedMotion =
    window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  /* ─── Dimensions ──────────────────────────────────────────────── */
  let width, height, dpr;

  function resize() {
    dpr = Math.min(window.devicePixelRatio || 1, 2);
    width = canvas.clientWidth;
    height = canvas.clientHeight;
    canvas.width = width * dpr;
    canvas.height = height * dpr;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  }
  resize();
  window.addEventListener("resize", resize);

  /* ─── Mouse tracking (lerped for smooth transitions) ───────── */
  let mouseX = width * 0.5;
  let mouseY = height * 0.5;
  let mouseTargetX = width * 0.5;
  let mouseTargetY = height * 0.5;
  const mouseLerp = 0.06;

  const heroSection = document.getElementById("hero");

  heroSection.addEventListener("mousemove", function (event) {
    const rect = heroSection.getBoundingClientRect();
    mouseTargetX = event.clientX - rect.left;
    mouseTargetY = event.clientY - rect.top;
  });

  heroSection.addEventListener("mouseleave", function () {
    mouseTargetX = width * 0.5;
    mouseTargetY = height * 0.5;
  });

  /* ─── Color palette ───────────────────────────────────────────── */
  const PURPLE = { r: 124, g: 58, b: 237 };   // #7c3aed
  const VIOLET = { r: 139, g: 92, b: 246 };    // #8b5cf6
  const GRAY   = { r: 45,  g: 43, b: 85 };     // #2d2b55
  const WHITE  = { r: 255, g: 255, b: 255 };

  /* ═══════════════════════════════════════════════════════════════
     LAYER 1 — Stars / Constellation Network
     ═══════════════════════════════════════════════════════════════ */
  const STAR_COUNT = Math.min(Math.floor(width * height / 4000), 200);
  const CONNECTION_DISTANCE = 140;

  const stars = [];
  for (let i = 0; i < STAR_COUNT; i++) {
    stars.push({
      x: Math.random() * width,
      y: Math.random() * height,
      baseX: 0,
      baseY: 0,
      radius: Math.random() * 1.6 + 0.4,
      speed: Math.random() * 0.15 + 0.02,
      angle: Math.random() * Math.PI * 2,
      depth: Math.random() * 0.5 + 0.5, // parallax depth
      twinklePhase: Math.random() * Math.PI * 2,
      twinkleSpeed: Math.random() * 0.02 + 0.005,
    });
  }
  // Store initial positions for parallax offset
  for (let i = 0; i < stars.length; i++) {
    stars[i].baseX = stars[i].x;
    stars[i].baseY = stars[i].y;
  }

  function updateStars(time) {
    const parallaxCenterX = width * 0.5;
    const parallaxCenterY = height * 0.5;
    const parallaxStrength = 20;

    for (let i = 0; i < stars.length; i++) {
      const star = stars[i];
      // Gentle orbital drift
      star.angle += star.speed * 0.01;
      const drift = Math.sin(star.angle) * 0.3;

      // Parallax from mouse
      const offsetX = ((mouseX - parallaxCenterX) / parallaxCenterX) * parallaxStrength * star.depth;
      const offsetY = ((mouseY - parallaxCenterY) / parallaxCenterY) * parallaxStrength * star.depth;

      star.x = star.baseX + drift + offsetX;
      star.y = star.baseY + drift * 0.5 + offsetY;

      // Twinkle
      star.twinklePhase += star.twinkleSpeed;
    }
  }

  function drawStars(time) {
    // Draw connections first (behind stars)
    ctx.lineWidth = 0.5;
    for (let i = 0; i < stars.length; i++) {
      for (let j = i + 1; j < stars.length; j++) {
        const dx = stars[i].x - stars[j].x;
        const dy = stars[i].y - stars[j].y;
        const distanceSquared = dx * dx + dy * dy;
        if (distanceSquared < CONNECTION_DISTANCE * CONNECTION_DISTANCE) {
          const distance = Math.sqrt(distanceSquared);
          const alpha = (1 - distance / CONNECTION_DISTANCE) * 0.15;
          ctx.strokeStyle = "rgba(" + VIOLET.r + "," + VIOLET.g + "," + VIOLET.b + "," + alpha + ")";
          ctx.beginPath();
          ctx.moveTo(stars[i].x, stars[i].y);
          ctx.lineTo(stars[j].x, stars[j].y);
          ctx.stroke();
        }
      }
    }

    // Draw stars
    for (let i = 0; i < stars.length; i++) {
      const star = stars[i];
      const twinkle = 0.4 + 0.6 * (0.5 + 0.5 * Math.sin(star.twinklePhase));
      const alpha = twinkle * star.depth;

      ctx.beginPath();
      ctx.arc(star.x, star.y, star.radius, 0, Math.PI * 2);
      ctx.fillStyle = "rgba(255,255,255," + alpha + ")";
      ctx.fill();

      // Glow for brighter stars
      if (star.radius > 1) {
        ctx.beginPath();
        ctx.arc(star.x, star.y, star.radius * 3, 0, Math.PI * 2);
        ctx.fillStyle = "rgba(" + VIOLET.r + "," + VIOLET.g + "," + VIOLET.b + "," + (alpha * 0.15) + ")";
        ctx.fill();
      }
    }
  }

  /* ═══════════════════════════════════════════════════════════════
     LAYER 2 — Volumetric Clouds (Simplex Noise)
     ═══════════════════════════════════════════════════════════════ */

  // Minimal 2D simplex noise implementation
  const GRAD = [[1,1],[-1,1],[1,-1],[-1,-1],[1,0],[-1,0],[0,1],[0,-1]];
  const perm = new Uint8Array(512);
  (function () {
    const p = new Uint8Array(256);
    for (let i = 0; i < 256; i++) p[i] = i;
    for (let i = 255; i > 0; i--) {
      const j = Math.floor(Math.random() * (i + 1));
      const tmp = p[i]; p[i] = p[j]; p[j] = tmp;
    }
    for (let i = 0; i < 512; i++) perm[i] = p[i & 255];
  })();

  function noise2d(x, y) {
    const F2 = 0.5 * (Math.sqrt(3) - 1);
    const G2 = (3 - Math.sqrt(3)) / 6;
    const s = (x + y) * F2;
    const i = Math.floor(x + s);
    const j = Math.floor(y + s);
    const t = (i + j) * G2;
    const X0 = i - t;
    const Y0 = j - t;
    const x0 = x - X0;
    const y0 = y - Y0;
    const i1 = x0 > y0 ? 1 : 0;
    const j1 = x0 > y0 ? 0 : 1;
    const x1 = x0 - i1 + G2;
    const y1 = y0 - j1 + G2;
    const x2 = x0 - 1 + 2 * G2;
    const y2 = y0 - 1 + 2 * G2;
    const ii = i & 255;
    const jj = j & 255;

    let n0 = 0, n1 = 0, n2 = 0;

    let t0 = 0.5 - x0 * x0 - y0 * y0;
    if (t0 > 0) {
      t0 *= t0;
      const gi0 = perm[ii + perm[jj]] & 7;
      n0 = t0 * t0 * (GRAD[gi0][0] * x0 + GRAD[gi0][1] * y0);
    }

    let t1 = 0.5 - x1 * x1 - y1 * y1;
    if (t1 > 0) {
      t1 *= t1;
      const gi1 = perm[ii + i1 + perm[jj + j1]] & 7;
      n1 = t1 * t1 * (GRAD[gi1][0] * x1 + GRAD[gi1][1] * y1);
    }

    let t2 = 0.5 - x2 * x2 - y2 * y2;
    if (t2 > 0) {
      t2 *= t2;
      const gi2 = perm[ii + 1 + perm[jj + 1]] & 7;
      n2 = t2 * t2 * (GRAD[gi2][0] * x2 + GRAD[gi2][1] * y2);
    }

    return 70 * (n0 + n1 + n2); // returns [-1, 1]
  }

  function fbm(x, y, octaves) {
    let value = 0;
    let amplitude = 1;
    let frequency = 1;
    let maxValue = 0;
    for (let i = 0; i < octaves; i++) {
      value += amplitude * noise2d(x * frequency, y * frequency);
      maxValue += amplitude;
      amplitude *= 0.5;
      frequency *= 2;
    }
    return value / maxValue;
  }

  // Cloud particles for a more organic look
  const CLOUD_COUNT = 18;
  const clouds = [];
  for (let i = 0; i < CLOUD_COUNT; i++) {
    clouds.push({
      x: Math.random() * width * 1.4 - width * 0.2,
      y: Math.random() * height,
      baseX: 0,
      baseY: 0,
      size: Math.random() * 200 + 120,
      driftSpeed: Math.random() * 0.2 + 0.05,
      noiseOffsetX: Math.random() * 100,
      noiseOffsetY: Math.random() * 100,
      opacity: Math.random() * 0.08 + 0.03,
    });
  }
  for (let i = 0; i < clouds.length; i++) {
    clouds[i].baseX = clouds[i].x;
    clouds[i].baseY = clouds[i].y;
  }

  function drawClouds(time) {
    const t = time * 0.0001;

    for (let i = 0; i < clouds.length; i++) {
      const cloud = clouds[i];

      // Slow drift
      cloud.x = cloud.baseX + Math.sin(t * cloud.driftSpeed + cloud.noiseOffsetX) * 30 + t * 8;

      // Wrap around
      if (cloud.x > width + cloud.size) {
        cloud.x -= width + cloud.size * 2;
        cloud.baseX = cloud.x;
      }

      // Mouse repulsion
      const dx = cloud.x - mouseX;
      const dy = cloud.y - mouseY;
      const dist = Math.sqrt(dx * dx + dy * dy);
      const repulsionRadius = 250;
      let repelX = 0, repelY = 0;
      if (dist < repulsionRadius && dist > 0) {
        const force = (1 - dist / repulsionRadius) * 40;
        repelX = (dx / dist) * force;
        repelY = (dy / dist) * force;
      }

      const drawX = cloud.x + repelX;
      const drawY = cloud.y + repelY;

      // Draw cloud as layered noise-modulated circles
      const steps = 6;
      for (let s = steps; s > 0; s--) {
        const ratio = s / steps;
        const radius = cloud.size * ratio;
        const noiseVal = fbm(
          (drawX + cloud.noiseOffsetX) * 0.003 + t * 0.5,
          (drawY + cloud.noiseOffsetY) * 0.003,
          3
        );
        const modulatedRadius = radius * (0.7 + noiseVal * 0.4);

        const gradient = ctx.createRadialGradient(drawX, drawY, 0, drawX, drawY, modulatedRadius);

        // Purple-gray cloud tones
        const purpleAmount = 0.3 + noiseVal * 0.3;
        const r = Math.floor(GRAY.r + (PURPLE.r - GRAY.r) * purpleAmount);
        const g = Math.floor(GRAY.g + (PURPLE.g - GRAY.g) * purpleAmount);
        const b = Math.floor(GRAY.b + (PURPLE.b - GRAY.b) * purpleAmount);

        gradient.addColorStop(0, "rgba(" + r + "," + g + "," + b + "," + (cloud.opacity * ratio) + ")");
        gradient.addColorStop(1, "rgba(" + r + "," + g + "," + b + ",0)");

        ctx.beginPath();
        ctx.arc(drawX, drawY, modulatedRadius, 0, Math.PI * 2);
        ctx.fillStyle = gradient;
        ctx.fill();
      }
    }
  }

  /* ═══════════════════════════════════════════════════════════════
     LAYER 3 — Lightning
     ═══════════════════════════════════════════════════════════════ */
  let lightningBolts = [];
  let nextLightningTime = 2000 + Math.random() * 4000;

  function createBolt(x, y, angle, depth, maxDepth) {
    if (depth > maxDepth) return [];

    const segments = [];
    const length = (80 + Math.random() * 120) / (depth * 0.7 + 1);
    const steps = Math.floor(length / 8) + 2;

    let currentX = x;
    let currentY = y;

    for (let i = 0; i < steps; i++) {
      const nextX = currentX + Math.cos(angle) * (length / steps) + (Math.random() - 0.5) * 12;
      const nextY = currentY + Math.sin(angle) * (length / steps) + (Math.random() - 0.5) * 6;
      segments.push({ x1: currentX, y1: currentY, x2: nextX, y2: nextY, depth: depth });
      currentX = nextX;
      currentY = nextY;

      // Branch chance
      if (depth < maxDepth && Math.random() < 0.25) {
        const branchAngle = angle + (Math.random() - 0.5) * 1.2;
        const branch = createBolt(currentX, currentY, branchAngle, depth + 1, maxDepth);
        for (let j = 0; j < branch.length; j++) segments.push(branch[j]);
      }
    }
    return segments;
  }

  function spawnLightning() {
    const startX = Math.random() * width;
    const startY = 0;
    const angle = Math.PI / 2 + (Math.random() - 0.5) * 0.6; // mostly downward
    const bolt = createBolt(startX, startY, angle, 0, 3);

    lightningBolts.push({
      segments: bolt,
      birth: performance.now(),
      duration: 150 + Math.random() * 200,
      flashIntensity: 0.6 + Math.random() * 0.4,
    });
  }

  function drawLightning(time) {
    const toRemove = [];

    for (let b = 0; b < lightningBolts.length; b++) {
      const bolt = lightningBolts[b];
      const age = time - bolt.birth;
      if (age > bolt.duration) {
        toRemove.push(b);
        continue;
      }

      const progress = age / bolt.duration;
      // Flash envelope: quick rise, slow fade
      const envelope = progress < 0.1 ? progress / 0.1 : 1 - (progress - 0.1) / 0.9;
      const alpha = envelope * bolt.flashIntensity;

      // Background flash
      ctx.fillStyle = "rgba(" + VIOLET.r + "," + VIOLET.g + "," + VIOLET.b + "," + (alpha * 0.04) + ")";
      ctx.fillRect(0, 0, width, height);

      for (let s = 0; s < bolt.segments.length; s++) {
        const seg = bolt.segments[s];
        const lineAlpha = alpha * (1 / (seg.depth * 0.5 + 1));
        const lineWidth = Math.max(0.5, 3 - seg.depth * 0.8);

        // Glow
        ctx.strokeStyle = "rgba(" + VIOLET.r + "," + VIOLET.g + "," + VIOLET.b + "," + (lineAlpha * 0.4) + ")";
        ctx.lineWidth = lineWidth + 4;
        ctx.beginPath();
        ctx.moveTo(seg.x1, seg.y1);
        ctx.lineTo(seg.x2, seg.y2);
        ctx.stroke();

        // Core
        ctx.strokeStyle = "rgba(255,255,255," + lineAlpha + ")";
        ctx.lineWidth = lineWidth;
        ctx.beginPath();
        ctx.moveTo(seg.x1, seg.y1);
        ctx.lineTo(seg.x2, seg.y2);
        ctx.stroke();
      }
    }

    // Remove expired bolts (reverse order)
    for (let i = toRemove.length - 1; i >= 0; i--) {
      lightningBolts.splice(toRemove[i], 1);
    }
  }

  /* ═══════════════════════════════════════════════════════════════
     ANIMATION LOOP
     ═══════════════════════════════════════════════════════════════ */
  let lastTime = 0;

  function animate(time) {
    requestAnimationFrame(animate);

    // Smoothly interpolate mouse position toward target
    mouseX += (mouseTargetX - mouseX) * mouseLerp;
    mouseY += (mouseTargetY - mouseY) * mouseLerp;

    ctx.clearRect(0, 0, width, height);

    if (prefersReducedMotion) {
      // Static render — just stars, no animation
      drawStars(0);
      return;
    }

    updateStars(time);
    drawStars(time);
    drawClouds(time);

    // Lightning scheduling
    if (time > nextLightningTime) {
      spawnLightning();
      nextLightningTime = time + 3000 + Math.random() * 6000;
    }
    drawLightning(time);

    lastTime = time;
  }

  requestAnimationFrame(animate);
})();
