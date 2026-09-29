"use strict";

(() => {
  const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
  const motionButton = document.querySelector("#motion-toggle");
  let motionPaused = reducedMotion.matches;
  let motionListener = () => {};

  function updateMotion() {
    document.documentElement.classList.toggle("motion-paused", motionPaused);
    motionButton.setAttribute("aria-pressed", String(motionPaused));
    motionButton.setAttribute(
      "aria-label",
      motionPaused ? "Resume animations" : "Pause animations",
    );
    motionButton.innerHTML = motionPaused
      ? '<span aria-hidden="true">▶</span> Resume motion'
      : '<span aria-hidden="true">Ⅱ</span> Pause motion';
    document.querySelectorAll("svg").forEach((svg) => {
      if (typeof svg.pauseAnimations === "function") {
        if (motionPaused) svg.pauseAnimations();
        else svg.unpauseAnimations();
      }
    });
    motionListener();
  }

  motionButton.addEventListener("click", () => {
    motionPaused = !motionPaused;
    updateMotion();
  });
  reducedMotion.addEventListener("change", (event) => {
    motionPaused = event.matches;
    updateMotion();
  });

  const cinema = document.querySelector(".cinema");
  const playButton = document.querySelector("#story-play");
  const seek = document.querySelector("#story-seek");
  const chapters = [...document.querySelectorAll(".story-chapters button")];
  const panels = [...document.querySelectorAll(".story-panel")];
  const nodes = [...document.querySelectorAll(".story-node")];
  const checks = [...document.querySelectorAll(".story-check")];
  const narrowStory = window.matchMedia("(max-width: 600px)");
  const wrongWire = document.querySelector(".story-wrong-wire");
  function updateStoryGeometry() {
    wrongWire.setAttribute(
      "d",
      narrowStory.matches
        ? "M576 483H668Q688 483 688 463V366"
        : "M526 483H678Q692 483 692 469V299Q692 285 678 285H668",
    );
  }
  updateStoryGeometry();
  const boundaries = [0, 8, 18, 30, 44];
  const landmarks = [6, 15, 28, 42];
  const storyCopy = [
    [
      "01 / See it",
      "Your code.<br>Connected.",
      "Roslyn reads the C#. Milligram reveals the components and their dependencies.",
    ],
    [
      "02 / Find the risk",
      "There’s the<br>trouble.",
      "Pricing reaches into Infrastructure. Weak tests and complex logic make it a risky place to change.",
    ],
    [
      "03 / Shape it",
      "Move the contract.<br>Change the picture.",
      "Explore a proposal with your agent. Put IPriceRepository in Domain. Turn the dependency inward.",
    ],
    [
      "04 / Make it better",
      "Change it.<br>Then prove it.",
      "Your agent implements the design, simplifies pricing, and adds tests. Fresh signals check the result.",
    ],
  ];
  let storyTime = reducedMotion.matches ? landmarks[0] : 0;
  let storyPlaying = !reducedMotion.matches;
  let storyVisible = false;
  let storyFrame = 0;
  let storyLastTime = 0;
  let shownChapter = -1;
  const clamp = (value) => Math.min(1, Math.max(0, value));
  const progress = (time, start, end) => clamp((time - start) / (end - start));
  const smooth = (value) => value * value * (3 - 2 * value);
  const storyElements = new Map();
  const textAt = (selector, value) => {
    if (!storyElements.has(selector))
      storyElements.set(selector, document.querySelector(selector));
    const element = storyElements.get(selector);
    if (element.textContent !== value) element.textContent = value;
  };
  const styleAt = (name, value) =>
    cinema.style.setProperty(name, String(value));

  function renderStory() {
    const t = storyTime;
    const chapter = t < 8 ? 0 : t < 18 ? 1 : t < 30 ? 2 : 3;
    if (chapter !== shownChapter) {
      shownChapter = chapter;
      cinema.dataset.chapter = String(chapter);
      const copy = storyCopy[chapter];
      textAt("#story-chapter-label", copy[0]);
      document.querySelector("#story-title").innerHTML = copy[1];
      textAt("#story-description", copy[2]);
      panels.forEach((panel, index) => (panel.hidden = index !== chapter));
      chapters.forEach((button, index) =>
        button.setAttribute("aria-pressed", String(index === chapter)),
      );
    }
    chapters.forEach((button, index) =>
      button.style.setProperty(
        "--chapter-progress",
        progress(t, boundaries[index], boundaries[index + 1]),
      ),
    );
    styleAt("--world-opacity", 1 - progress(t, 43, 44));
    styleAt("--source-opacity", 1 - progress(t, 1.1, 3.5));
    styleAt("--source-y", `${-smooth(progress(t, 1.1, 3.5)) * 30}px`);
    styleAt(
      "--scan-opacity",
      progress(t, 0.3, 1) * (1 - progress(t, 4.4, 5.1)),
    );
    styleAt("--scan-y", `${progress(t, 0.5, 4.8) * 91}%`);
    nodes.forEach((node, index) => {
      const p = smooth(progress(t, 1 + index * 0.45, 2.5 + index * 0.45));
      node.style.setProperty("--node-opacity", p);
      node.style.setProperty("--node-y", `${(1 - p) * 25}px`);
    });
    styleAt("--wire-draw", 1 - progress(t, 2.6, 5.8));
    styleAt("--wire-opacity", progress(t, 2.6, 3.4));
    const risk = progress(t, 8.4, 9.7) * (1 - progress(t, 23.8, 26.4));
    styleAt("--risk-opacity", risk);
    styleAt(
      "--risk-label",
      progress(t, 10.8, 12) * (1 - progress(t, 20.5, 22)),
    );
    styleAt("--risk-glow", risk * (0.6 + Math.sin(t * 2.8) * 0.25));
    styleAt("--wrong-draw", 1 - progress(t, 9, 11.5));
    styleAt("--right-draw", 1 - progress(t, 25.5, 28));
    styleAt("--right-opacity", progress(t, 25.5, 26.3));
    const move = smooth(progress(t, 22, 26));
    const narrow = narrowStory.matches;
    const startX = narrow ? 57 : 64;
    const endX = narrow ? 30 : 37;
    styleAt(
      "--contract-x",
      `${startX + (endX - startX) * move - Math.sin(move * Math.PI) * 8}%`,
    );
    styleAt("--contract-y", `${51.3 + 36.7 * move}%`);
    styleAt("--contract-opacity", progress(t, 3.8, 4.8));
    styleAt("--agent-opacity", progress(t, 19.4, 20.1));
    const agentText = "Move IPriceRepository inward. Keep the adapter outside.";
    textAt(
      "#story-agent-response",
      agentText.slice(0, Math.ceil(progress(t, 19.6, 23) * agentText.length)) ||
        "…",
    );
    checks.forEach((check, index) => {
      const done = t >= [32.3, 35.3, 38][index];
      check.classList.toggle("done", done);
      const icon = done ? "✓" : "○";
      if (check.firstElementChild.textContent !== icon)
        check.firstElementChild.textContent = icon;
    });
    styleAt("--outcome-opacity", progress(t, 39.5, 40.5));
    const measured = t >= 38;
    cinema.dataset.measured = String(measured);
    const improving = smooth(progress(t, 38, 40.5));
    const values = [
      ["violations", t < 10 ? "—" : t >= 32.3 ? "0" : "1"],
      ["crap", t < 12 ? "—" : (46.3 - 42.3 * improving).toFixed(1)],
      ["coverage", t < 12.7 ? "—" : `${Math.round(38 + 56 * improving)}%`],
      ["mutation", t < 13.4 ? "—" : `${Math.round(41 + 51 * improving)}%`],
    ];
    values.forEach(([key, value]) => {
      textAt(`#metric-${key}`, value);
      document.querySelector(`#baseline-${key}`).hidden =
        key === "violations" ? t < 32.3 : !measured;
    });
    textAt(
      "#story-metric-status",
      t < 12
        ? "Waiting for the signals"
        : t < 30
          ? "Before the change"
          : t < 38
            ? "Refreshing after code + tests"
            : "After · illustrative results",
    );
    textAt(
      "#story-context",
      t < 5
        ? "Reading the source"
        : t < 8
          ? "Real architecture"
          : t < 18
            ? "Risk in focus"
            : t < 30
              ? "Proposal · Isolate pricing"
              : t < 38
                ? "Implementing & testing"
                : "Real architecture · rechecked",
    );
    textAt(
      "#story-map-caption",
      t < 8
        ? "Source files become a system."
        : t < 18
          ? "An inner rule depends on an outer detail."
          : t < 22
            ? "Same code. A different possibility."
            : t < 30
              ? "Move the contract. Turn the dependency inward."
              : t < 38
                ? "The agent changes the code. The map follows."
                : "A cleaner boundary. Stronger tests. Measurable change.",
    );
    const traveller = document.querySelector(".story-traveller");
    const wire = document.querySelector(
      t >= 26 ? ".story-right-wire" : ".story-base-wires path",
    );
    const position = wire.getPointAtLength(
      wire.getTotalLength() * ((t % 3) / 3),
    );
    traveller.setAttribute("cx", position.x);
    traveller.setAttribute("cy", position.y);
    styleAt("--traveller-opacity", (t > 5 && t < 8) || t > 28 ? 0.9 : 0);
    seek.value = String(t);
    const seconds = Math.floor(t);
    seek.setAttribute(
      "aria-valuetext",
      `${seconds} seconds of 44. ${storyCopy[chapter][0]}`,
    );
    textAt("#story-time", `00:${String(seconds).padStart(2, "0")} / 00:44`);
  }

  function updateStoryPlayback() {
    if (storyFrame) cancelAnimationFrame(storyFrame);
    storyFrame = 0;
    storyLastTime = 0;
    const active = storyPlaying && !motionPaused;
    playButton.innerHTML = active
      ? "Ⅱ <span>Pause</span>"
      : "▶ <span>Play</span>";
    playButton.setAttribute(
      "aria-label",
      active ? "Pause story" : "Play story",
    );
    playButton.setAttribute("aria-pressed", String(active));
    cinema.classList.toggle("story-paused", !active);
    if (active && storyVisible && !document.hidden)
      storyFrame = requestAnimationFrame(tickStory);
  }

  function tickStory(timestamp) {
    storyFrame = 0;
    if (!storyPlaying || motionPaused || !storyVisible || document.hidden)
      return;
    if (storyLastTime)
      storyTime += Math.min((timestamp - storyLastTime) / 1000, 0.1);
    storyLastTime = timestamp;
    if (storyTime >= 44) storyTime %= 44;
    renderStory();
    storyFrame = requestAnimationFrame(tickStory);
  }

  function seekStory(time, announce = false) {
    storyTime = Math.min(43.99, Math.max(0, time));
    renderStory();
    updateStoryPlayback();
    if (announce)
      textAt(
        "#story-status",
        `${storyCopy[shownChapter][0]}. ${storyCopy[shownChapter][2]}`,
      );
  }

  playButton.addEventListener("click", () => {
    if (motionPaused) {
      storyPlaying = true;
      motionPaused = false;
      updateMotion();
    } else {
      storyPlaying = !storyPlaying;
      updateStoryPlayback();
    }
  });
  document.querySelector("#story-replay").addEventListener("click", () => {
    storyPlaying = !reducedMotion.matches;
    seekStory(reducedMotion.matches ? landmarks[0] : 0, true);
  });
  seek.addEventListener("input", () => {
    storyPlaying = false;
    seekStory(Number(seek.value));
  });
  chapters.forEach((button, index) =>
    button.addEventListener("click", () => {
      storyPlaying = false;
      seekStory(landmarks[index], true);
    }),
  );
  document
    .querySelector('.hero-actions a[href="#how-it-works"]')
    .addEventListener("click", () => {
      storyPlaying = !motionPaused;
      seekStory(motionPaused ? landmarks[0] : 0);
    });
  new IntersectionObserver(
    (entries) => {
      storyVisible = entries[0].isIntersecting;
      updateStoryPlayback();
    },
    { threshold: 0.08 },
  ).observe(cinema);
  narrowStory.addEventListener("change", () => {
    updateStoryGeometry();
    renderStory();
  });
  renderStory();

  const copyButton = document.querySelector("#copy-command");
  const copyStatus = document.querySelector("#copy-status");
  let copyTimer;
  copyButton.addEventListener("click", async () => {
    clearTimeout(copyTimer);
    try {
      await navigator.clipboard.writeText("milligram");
      copyButton.textContent = "Copied ✓";
      copyStatus.textContent =
        "Command copied. Run it in your C# project after installation.";
    } catch {
      copyStatus.textContent = "Select and copy the command above: milligram";
    }
    copyTimer = setTimeout(() => {
      copyButton.innerHTML = 'Copy <span aria-hidden="true">⧉</span>';
      copyStatus.textContent = "";
    }, 5000);
  });

  // The illustration uses a fixed world so resizing never changes its topology.
  const canvas = document.querySelector("#architecture-art");
  const ctx = canvas.getContext("2d");
  let artVisible = true;
  let frame = 0;
  let lastTime = 0;
  let elapsed = 0;
  let canvasWidth = 600;
  let canvasHeight = 525;
  const groups = [
    { x: 0, y: 0, w: 4, h: 4, tone: 0 },
    { x: 7, y: 0, w: 6, h: 5, tone: 1 },
    { x: 0, y: 6, w: 6, h: 6, tone: 0 },
    { x: 8, y: 8, w: 7, h: 6, tone: 2 },
  ];
  const colors = [
    ["#b9d2ff", "#477bff", "#7da5ff"],
    ["#a2c2ff", "#305eee", "#668cff"],
    ["#d9ff5c", "#80b52c", "#b3df3d"],
  ];
  const blocks = [];
  const random = (value) => {
    const n = Math.sin(value * 127.1 + 311.7) * 43758.5453;
    return n - Math.floor(n);
  };
  groups.forEach((group, groupIndex) => {
    for (let row = 0; row < group.h; row++) {
      for (let col = 0; col < group.w; col++) {
        const id = blocks.length;
        const height = 9 + Math.floor(random(id + 4) * 27);
        blocks.push({
          x: group.x + col,
          y: group.y + row,
          group: groupIndex,
          id,
          height,
          risk: groupIndex === 1 && col === 1 && row === 3,
          originX: (random(id + 62) - 0.5) * 280,
          originY: (random(id + 83) - 0.5) * 240,
        });
      }
    }
  });
  blocks.sort((a, b) => a.x + a.y - b.x - b.y);
  const iso = (x, y, z = 0) => ({
    x: 291 + (x - y) * 18,
    y: 112 + (x + y) * 9 - z,
  });

  function polygon(points, fill, stroke = "#3055a2", alpha = 1) {
    ctx.globalAlpha = alpha;
    ctx.beginPath();
    points.forEach((point, index) =>
      index ? ctx.lineTo(point.x, point.y) : ctx.moveTo(point.x, point.y),
    );
    ctx.closePath();
    ctx.fillStyle = fill;
    ctx.fill();
    if (stroke) {
      ctx.strokeStyle = stroke;
      ctx.lineWidth = 0.6;
      ctx.stroke();
    }
    ctx.globalAlpha = 1;
  }

  function drawBlock(block, time) {
    const intro = motionPaused
      ? 1
      : Math.min(1, Math.max(0, (time - block.id * 0.005) / 1.8));
    const settle = 1 - Math.pow(1 - intro, 4);
    const breathe = motionPaused
      ? 0
      : Math.sin(time * 0.65 + block.group * 1.4) * 2;
    const z = block.height + breathe;
    const offsetX = block.originX * (1 - settle);
    const offsetY = block.originY * (1 - settle);
    const point = (x, y, height) => {
      const p = iso(x, y, height);
      return { x: p.x + offsetX, y: p.y + offsetY };
    };
    const x = block.x,
      y = block.y,
      size = 0.83;
    const top = [
      point(x, y, z),
      point(x + size, y, z),
      point(x + size, y + size, z),
      point(x, y + size, z),
    ];
    const palette = block.risk
      ? ["#ff9569", "#d73715", "#ff6537"]
      : colors[groups[block.group].tone];
    const edge = block.risk ? "#c4381c" : "#264582";
    polygon(
      [top[1], point(x + size, y, 0), point(x + size, y + size, 0), top[2]],
      palette[1],
      edge,
      0.9 * intro,
    );
    polygon(
      [top[2], point(x + size, y + size, 0), point(x, y + size, 0), top[3]],
      palette[2],
      edge,
      0.95 * intro,
    );
    polygon(top, palette[0], edge, intro);
    if (block.id % 5 === 0) {
      const p = point(x + 0.33, y + 0.3, z + 0.1);
      ctx.fillStyle = block.risk ? "#b52b08" : "#315ac6";
      ctx.globalAlpha = 0.7 * intro;
      ctx.fillRect(p.x, p.y, 3.5, 1.2);
      ctx.globalAlpha = 1;
    }
  }

  const links = [
    [2, 2, 9, 2],
    [2, 2, 3, 8],
    [3, 8, 10, 10],
    [9, 2, 10, 10],
  ];
  function draw(time) {
    if (!ctx) return;
    ctx.clearRect(0, 0, canvasWidth, canvasHeight);
    ctx.save();
    const scale = Math.min(canvasWidth / 580, canvasHeight / 525);
    ctx.translate(
      (canvasWidth - 580 * scale) / 2,
      (canvasHeight - 525 * scale) / 2,
    );
    ctx.scale(scale, scale);
    groups.forEach((group) => {
      const x = group.x - 0.4,
        y = group.y - 0.4,
        w = group.w + 0.7,
        h = group.h + 0.7;
      polygon(
        [
          iso(x, y, -3),
          iso(x + w, y, -3),
          iso(x + w, y + h, -3),
          iso(x, y + h, -3),
        ],
        "#ceddff88",
        "#6a92ed99",
      );
      polygon(
        [
          iso(x + w, y, -3),
          iso(x + w, y + h, -3),
          iso(x, y + h, -3),
          iso(x, y + h, -7),
          iso(x + w, y + h, -7),
          iso(x + w, y, -7),
        ],
        "#96b6ff66",
        "#6a92ed66",
      );
    });
    links.forEach((link, index) => {
      const from = iso(link[0], link[1], -5),
        to = iso(link[2], link[3], -5);
      ctx.beginPath();
      ctx.moveTo(from.x, from.y);
      ctx.lineTo(to.x, to.y);
      ctx.strokeStyle = "#2457ffbb";
      ctx.lineWidth = 1;
      ctx.setLineDash([3, 4]);
      ctx.stroke();
      ctx.setLineDash([]);
      const t = motionPaused ? 0.5 : (time * 0.16 + index * 0.22) % 1;
      ctx.fillStyle = "#2457ff";
      ctx.beginPath();
      ctx.arc(
        from.x + (to.x - from.x) * t,
        from.y + (to.y - from.y) * t,
        2,
        0,
        Math.PI * 2,
      );
      ctx.fill();
    });
    blocks.forEach((block) => drawBlock(block, time));
    const from = iso(12, 9, 25),
      to = iso(8, 3, 29);
    const bend = { x: from.x + 90, y: (from.y + to.y) / 2 };
    ctx.beginPath();
    ctx.moveTo(from.x, from.y);
    ctx.quadraticCurveTo(bend.x, bend.y, to.x, to.y);
    ctx.strokeStyle = "#f34a20";
    ctx.lineWidth = 1.1;
    ctx.setLineDash([3, 3]);
    ctx.stroke();
    ctx.setLineDash([]);
    const t = motionPaused ? 0.6 : (time * 0.23) % 1;
    const p = {
      x: (1 - t) ** 2 * from.x + 2 * (1 - t) * t * bend.x + t * t * to.x,
      y: (1 - t) ** 2 * from.y + 2 * (1 - t) * t * bend.y + t * t * to.y,
    };
    ctx.fillStyle = "#f34a20";
    ctx.beginPath();
    ctx.arc(p.x, p.y, 3, 0, Math.PI * 2);
    ctx.fill();
    ctx.strokeStyle = "#f34a2066";
    ctx.lineWidth = 4;
    ctx.stroke();
    ctx.restore();
  }

  function tick(timestamp) {
    frame = 0;
    if (!artVisible || document.hidden || motionPaused) {
      lastTime = 0;
      return;
    }
    if (lastTime) elapsed += Math.min((timestamp - lastTime) / 1000, 0.06);
    lastTime = timestamp;
    draw(elapsed);
    frame = requestAnimationFrame(tick);
  }

  function resumeArt() {
    if (frame) cancelAnimationFrame(frame);
    frame = 0;
    lastTime = 0;
    if (motionPaused) draw(Math.max(elapsed, 4));
    else if (artVisible && !document.hidden)
      frame = requestAnimationFrame(tick);
  }

  function resizeArt() {
    const bounds = canvas.getBoundingClientRect();
    canvasWidth = bounds.width;
    canvasHeight = bounds.height;
    const ratio = Math.min(window.devicePixelRatio || 1, 2);
    canvas.width = Math.round(canvasWidth * ratio);
    canvas.height = Math.round(canvasHeight * ratio);
    ctx?.setTransform(ratio, 0, 0, ratio, 0, 0);
    draw(motionPaused ? Math.max(elapsed, 4) : elapsed);
  }

  new ResizeObserver(resizeArt).observe(canvas);
  new IntersectionObserver(
    (entries) => {
      artVisible = entries[0].isIntersecting;
      resumeArt();
    },
    { threshold: 0 },
  ).observe(canvas);

  motionListener = () => {
    updateStoryPlayback();
    resumeArt();
  };
  document.addEventListener("visibilitychange", () => {
    updateStoryPlayback();
    resumeArt();
  });
  updateMotion();
})();
