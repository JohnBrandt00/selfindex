window.vizCanvas = (() => {
  let _dotnet = null;
  let _points = [];
  let _canvas = null;
  let _transform = { scale: 1, offsetX: 0, offsetY: 0 };
  let _dragging = false;
  let _dragStart = null;
  let _lastHovered = null;
  const RADIUS = 5;
  const HOVER_RADIUS = 9;

  function draw(canvasId, wrapperId, points, dotnetRef) {
    _dotnet = dotnetRef;
    _points = points;
    _canvas = document.getElementById(canvasId);
    if (!_canvas) return;

    const wrap = document.getElementById(wrapperId);
    _canvas.width  = wrap.clientWidth;
    _canvas.height = wrap.clientHeight;

    attachEvents();
    render();
  }

  function toScreen(nx, ny) {
    const W = _canvas.width, H = _canvas.height;
    const pad = 40;
    const x = pad + nx * (W - 2 * pad);
    const y = pad + (1 - ny) * (H - 2 * pad); // flip Y so up = high PC2
    return [
      _transform.offsetX + (x - _transform.offsetX) * _transform.scale + (_canvas.width  / 2) * (1 - _transform.scale),
      _transform.offsetY + (y - _transform.offsetY) * _transform.scale + (_canvas.height / 2) * (1 - _transform.scale)
    ];
  }

  function toNorm(sx, sy) {
    const W = _canvas.width, H = _canvas.height;
    const pad = 40;
    const x = (sx - _canvas.width  / 2) / _transform.scale - _transform.offsetX + _canvas.width  / 2;
    const y = (sy - _canvas.height / 2) / _transform.scale - _transform.offsetY + _canvas.height / 2;
    return [(x - pad) / (W - 2 * pad), 1 - (y - pad) / (H - 2 * pad)];
  }

  function render(hoveredKey) {
    if (!_canvas) return;
    const ctx = _canvas.getContext('2d');
    ctx.clearRect(0, 0, _canvas.width, _canvas.height);

    // Dark background
    ctx.fillStyle = '#0d1117';
    ctx.fillRect(0, 0, _canvas.width, _canvas.height);

    // Grid lines
    ctx.strokeStyle = '#21262d';
    ctx.lineWidth = 1;
    const gridStep = 0.1;
    for (let t = 0; t <= 1.0001; t += gridStep) {
      const [x0] = toScreen(t, 0), [x1] = toScreen(t, 1);
      const [, y0] = toScreen(0, t), [, y1] = toScreen(1, t);
      ctx.beginPath(); ctx.moveTo(x0, _canvas.height - 40 * _transform.scale); ctx.lineTo(x0, 40 * _transform.scale); ctx.stroke();
      ctx.beginPath(); ctx.moveTo(40 * _transform.scale, y0); ctx.lineTo(_canvas.width - 40 * _transform.scale, y0); ctx.stroke();
    }

    // Draw points — two passes: normal first, then hovered on top
    for (const p of _points) {
      if (p.key === hoveredKey) continue;
      const [sx, sy] = toScreen(p.x, p.y);
      ctx.beginPath();
      ctx.arc(sx, sy, RADIUS, 0, Math.PI * 2);
      ctx.fillStyle = p.color + 'cc';
      ctx.fill();
    }

    // Hovered point
    if (hoveredKey) {
      const hp = _points.find(p => p.key === hoveredKey);
      if (hp) {
        const [sx, sy] = toScreen(hp.x, hp.y);

        // Glow ring
        const grad = ctx.createRadialGradient(sx, sy, HOVER_RADIUS, sx, sy, HOVER_RADIUS * 3);
        grad.addColorStop(0, hp.color + '66');
        grad.addColorStop(1, 'transparent');
        ctx.beginPath();
        ctx.arc(sx, sy, HOVER_RADIUS * 3, 0, Math.PI * 2);
        ctx.fillStyle = grad;
        ctx.fill();

        ctx.beginPath();
        ctx.arc(sx, sy, HOVER_RADIUS, 0, Math.PI * 2);
        ctx.fillStyle = hp.color;
        ctx.strokeStyle = '#fff';
        ctx.lineWidth = 2;
        ctx.fill();
        ctx.stroke();

        // Tooltip
        const label = hp.label || hp.key.split(':')[0].split(/[/\\]/).pop();
        const tx = sx + 14, ty = sy - 8;
        ctx.font = '12px "Segoe UI", monospace';
        const tw = ctx.measureText(label).width;
        ctx.fillStyle = '#161b22ee';
        ctx.beginPath();
        ctx.roundRect(tx - 4, ty - 14, tw + 12, 22, 4);
        ctx.fill();
        ctx.fillStyle = '#e6edf3';
        ctx.fillText(label, tx + 2, ty + 2);
      }
    }
  }

  function findNearest(mx, my) {
    let best = null, bestDist = HOVER_RADIUS * HOVER_RADIUS * 4;
    for (const p of _points) {
      const [sx, sy] = toScreen(p.x, p.y);
      const d = (sx - mx) ** 2 + (sy - my) ** 2;
      if (d < bestDist) { bestDist = d; best = p; }
    }
    return best;
  }

  function attachEvents() {
    // Remove old listeners by cloning
    const c = _canvas;
    const nc = c.cloneNode(true);
    c.parentNode.replaceChild(nc, c);
    _canvas = nc;

    _canvas.addEventListener('mousemove', e => {
      const r = _canvas.getBoundingClientRect();
      const mx = e.clientX - r.left, my = e.clientY - r.top;

      if (_dragging) {
        _transform.offsetX += mx - _dragStart.x;
        _transform.offsetY += my - _dragStart.y;
        _dragStart = { x: mx, y: my };
        render(_lastHovered);
        return;
      }

      const hit = findNearest(mx, my);
      const key = hit ? hit.key : null;
      if (key !== _lastHovered) {
        _lastHovered = key;
        render(key);
        if (_dotnet) _dotnet.invokeMethodAsync('OnHover', key ?? '');
      }
    });

    _canvas.addEventListener('mouseleave', () => {
      _lastHovered = null;
      render(null);
      if (_dotnet) _dotnet.invokeMethodAsync('OnHover', '');
    });

    _canvas.addEventListener('mousedown', e => {
      _dragging = true;
      const r = _canvas.getBoundingClientRect();
      _dragStart = { x: e.clientX - r.left, y: e.clientY - r.top };
      _canvas.style.cursor = 'grabbing';
    });

    _canvas.addEventListener('mouseup', () => {
      _dragging = false;
      _canvas.style.cursor = 'crosshair';
    });

    _canvas.addEventListener('wheel', e => {
      e.preventDefault();
      const factor = e.deltaY < 0 ? 1.12 : 0.89;
      const r = _canvas.getBoundingClientRect();
      const mx = e.clientX - r.left, my = e.clientY - r.top;

      _transform.offsetX = mx - (mx - _transform.offsetX) * factor;
      _transform.offsetY = my - (my - _transform.offsetY) * factor;
      _transform.scale  *= factor;
      _transform.scale   = Math.min(Math.max(_transform.scale, 0.2), 30);
      render(_lastHovered);
    }, { passive: false });

    // Reset on double-click
    _canvas.addEventListener('dblclick', () => {
      _transform = { scale: 1, offsetX: 0, offsetY: 0 };
      render(_lastHovered);
    });

    _canvas.style.cursor = 'crosshair';
    window.addEventListener('resize', () => {
      const wrap = _canvas.parentElement;
      _canvas.width  = wrap.clientWidth;
      _canvas.height = wrap.clientHeight;
      render(_lastHovered);
    });
  }

  return { draw };
})();
