window.viz3d = (() => {
  let _scene, _camera, _renderer, _controls;
  let _pointsMesh = null;
  let _queryMesh = null, _ringMeshes = [], _queryLabel = null;
  let _points = [], _dotnet = null;
  let _raycaster, _mouse;
  let _lastHoveredIdx = -1;
  let _container = null;
  let _animId = null;

  async function init(containerId, points, dotnetRef) {
    if (!window.THREE) { console.error('viz3d: THREE not loaded'); return; }

    _dotnet = dotnetRef;
    _points = points;
    _container = document.getElementById(containerId);
    if (!_container) { console.error('viz3d: container not found:', containerId); return; }

    // Tear down previous instance
    if (_renderer) {
      cancelAnimationFrame(_animId);
      _renderer.dispose();
      _container.innerHTML = '';
      _pointsMesh = null; _queryMesh = null; _ringMeshes = []; _queryLabel = null;
      _lastHoveredIdx = -1;
    }

    // Wait one frame for layout
    await new Promise(r => requestAnimationFrame(r));
    let W = _container.clientWidth, H = _container.clientHeight;
    if (W < 10 || H < 10) {
      await new Promise(r => setTimeout(r, 120));
      W = _container.clientWidth; H = _container.clientHeight;
    }
    if (W < 10) W = 900; if (H < 10) H = 600;

    const T = THREE;

    _scene = new T.Scene();
    _scene.background = new T.Color(0x0d1117);

    _camera = new T.PerspectiveCamera(60, W / H, 0.01, 100);
    _camera.position.set(0, 0, 3.5);

    _renderer = new T.WebGLRenderer({ antialias: true });
    _renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    _renderer.setSize(W, H);
    _container.appendChild(_renderer.domElement);

    _controls = new THREE.OrbitControls(_camera, _renderer.domElement);
    _controls.enableDamping = true;
    _controls.dampingFactor = 0.08;
    _controls.minDistance = 0.3;
    _controls.maxDistance = 20;

    addGrid(T);
    buildPointCloud(T, points);

    _raycaster = new T.Raycaster();
    _raycaster.params.Points = { threshold: 0.05 };
    _mouse = new T.Vector2(-9, -9);

    _renderer.domElement.addEventListener('mousemove', onMouseMove);
    _renderer.domElement.addEventListener('click', onClick);
    _renderer.domElement.addEventListener('dblclick', () => {
      _camera.position.set(0, 0, 3.5);
      _controls.target.set(0, 0, 0);
    });
    window.addEventListener('resize', onResize);

    animate();
  }

  function addGrid(T) {
    const mat = new T.LineBasicMaterial({ color: 0x21262d, transparent: true, opacity: 0.6 });
    for (let i = -1; i <= 1.01; i += 0.5) {
      addLine(T, [-1,i,-1], [1,i,-1], mat);
      addLine(T, [i,-1,-1], [i,1,-1], mat);
    }
    addLine(T, [0,0,0],[1,0,0], new T.LineBasicMaterial({ color: 0xff4444, transparent:true, opacity:0.35 }));
    addLine(T, [0,0,0],[0,1,0], new T.LineBasicMaterial({ color: 0x44cc44, transparent:true, opacity:0.35 }));
    addLine(T, [0,0,0],[0,0,1], new T.LineBasicMaterial({ color: 0x4488ff, transparent:true, opacity:0.35 }));
  }

  function addLine(T, a, b, mat) {
    const g = new T.BufferGeometry().setFromPoints([new T.Vector3(...a), new T.Vector3(...b)]);
    _scene.add(new T.Line(g, mat));
  }

  function buildPointCloud(T, points) {
    if (_pointsMesh) { _scene.remove(_pointsMesh); _pointsMesh.geometry.dispose(); _pointsMesh.material.dispose(); }

    const n   = points.length;
    const pos = new Float32Array(n * 3);
    const col = new Float32Array(n * 3);
    const sz  = new Float32Array(n);
    const idx = new Float32Array(n);
    const tmp = new T.Color();

    points.forEach((p, i) => {
      pos[i*3] = p.x; pos[i*3+1] = p.y; pos[i*3+2] = p.z;
      tmp.set(p.color);
      col[i*3] = tmp.r; col[i*3+1] = tmp.g; col[i*3+2] = tmp.b;
      sz[i]  = 7;
      idx[i] = i;
    });

    const geo = new T.BufferGeometry();
    geo.setAttribute('position', new T.BufferAttribute(pos, 3));
    geo.setAttribute('color',    new T.BufferAttribute(col, 3));
    geo.setAttribute('size',     new T.BufferAttribute(sz,  1));
    geo.setAttribute('idx',      new T.BufferAttribute(idx, 1));

    const mat = new T.ShaderMaterial({
      vertexColors: true,
      transparent: true,
      uniforms: { hovered: { value: -1.0 } },
      vertexShader: `
        attribute float size;
        attribute float idx;
        uniform float hovered;
        varying vec3 vColor;
        varying float vHov;
        void main() {
          vColor = color;
          vHov   = (abs(idx - hovered) < 0.5) ? 1.0 : 0.0;
          gl_PointSize = size * (vHov > 0.5 ? 2.5 : 1.0);
          gl_Position  = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
        }
      `,
      fragmentShader: `
        varying vec3 vColor;
        varying float vHov;
        void main() {
          vec2 uv = gl_PointCoord - 0.5;
          float d = length(uv);
          if (d > 0.5) discard;
          float alpha = 1.0 - smoothstep(0.3, 0.5, d);
          vec3 col = vHov > 0.5 ? mix(vColor, vec3(1.0), 0.5) : vColor;
          gl_FragColor = vec4(col, alpha * (vHov > 0.5 ? 1.0 : 0.85));
        }
      `
    });

    _pointsMesh = new T.Points(geo, mat);
    _scene.add(_pointsMesh);
  }

  function setQuery(qx, qy, qz, label, pointsWithSim) {
    // Build key→sim lookup — do NOT replace _points (it must stay in sync with _pointsMesh geometry indices)
    const simByKey = {};
    pointsWithSim.forEach(p => { simByKey[p.key] = p.sim ?? 0; });

    clearQuery();
    const T = THREE;

    const star = new T.Mesh(
      new T.SphereGeometry(0.022, 16, 16),
      new T.MeshBasicMaterial({ color: 0x58a6ff })
    );
    star.position.set(qx, qy, qz);
    _queryMesh = star;
    _scene.add(star);

    // Draw lines from query point to the top-N most similar points.
    // Use _points (same array as _pointsMesh geometry) so x/y/z are correct.
    const allWithSim = _points
      .map(p => ({ p, sim: simByKey[p.key] ?? 0 }))
      .sort((a, b) => b.sim - a.sim);
    const topSim = allWithSim[0]?.sim ?? 0;
    const sorted = allWithSim
      .filter(x => x.sim >= topSim * 0.5)
      .slice(0, 15);


    sorted.forEach(({ p, sim }, rank) => {
      const linePts = [new T.Vector3(qx, qy, qz), new T.Vector3(p.x, p.y, p.z)];
      const geo = new T.BufferGeometry().setFromPoints(linePts);
      const opacity = rank < 5 ? 0.7 : Math.max(0.15, 0.5 - rank * 0.03);
      const color   = rank < 5 ? 0x58a6ff : 0x388bfd;
      const mat = new T.LineBasicMaterial({ color, transparent: true, opacity });
      _ringMeshes.push(new T.Line(geo, mat));
      _scene.add(_ringMeshes[_ringMeshes.length - 1]);

      if (rank < 5) {
        // Large bright orange dot so it's unmistakable
        const dot = new T.Mesh(
          new T.SphereGeometry(0.035, 12, 12),
          new T.MeshBasicMaterial({ color: 0xff6600 })
        );
        dot.position.set(p.x, p.y, p.z);
        _ringMeshes.push(dot);
        _scene.add(dot);
      }
    });

    _queryLabel = makeSprite(T, label, '#58a6ff');
    _queryLabel.position.set(qx, qy + 0.13, qz);
    _scene.add(_queryLabel);

    if (_pointsMesh) {
      const colAttr = _pointsMesh.geometry.attributes.color;
      const sims = _points.map(p => simByKey[p.key] ?? 0);
      const minSim = Math.min(...sims), maxSim = Math.max(...sims);
      const range = Math.max(maxSim - minSim, 0.001);
      const tmp = new T.Color(), grey = new T.Color(0.18, 0.18, 0.20);
      _points.forEach((p, i) => {
        const t = ((sims[i] - minSim) / range) ** 1.5;
        tmp.set(p.color);
        const m = grey.clone().lerp(tmp, Math.max(0.15, t));
        colAttr.setXYZ(i, m.r, m.g, m.b);
      });
      colAttr.needsUpdate = true;
    }

    flyTo(new T.Vector3(qx, qy, qz), 2.2);
  }

  function clearQuery() {
    if (_queryMesh)  { _scene.remove(_queryMesh);  _queryMesh.geometry.dispose();  _queryMesh = null; }
    if (_queryLabel) { _scene.remove(_queryLabel); _queryLabel = null; }
    _ringMeshes.forEach(r => { _scene.remove(r); r.geometry?.dispose(); r.material?.dispose(); });
    _ringMeshes = [];
    if (_pointsMesh) {
      const colAttr = _pointsMesh.geometry.attributes.color;
      const tmp = new THREE.Color();
      _points.forEach((p, i) => { tmp.set(p.color); colAttr.setXYZ(i, tmp.r, tmp.g, tmp.b); });
      colAttr.needsUpdate = true;
    }
  }

  function makeSprite(T, text, color) {
    const c = document.createElement('canvas');
    c.width = 320; c.height = 64;
    const ctx = c.getContext('2d');
    ctx.font = 'bold 24px "Segoe UI", sans-serif';
    ctx.fillStyle = color;
    ctx.fillText(text.substring(0, 26), 6, 44);
    const spr = new T.Sprite(new T.SpriteMaterial({ map: new T.CanvasTexture(c), transparent: true }));
    spr.scale.set(0.7, 0.14, 1);
    return spr;
  }

  function flyTo(target, dist) {
    const start = _camera.position.clone();
    const dir   = start.clone().sub(_controls.target).normalize();
    const end   = target.clone().add(dir.multiplyScalar(dist));
    let t = 0;
    const step = () => {
      t = Math.min(t + 0.035, 1);
      const e = t < 0.5 ? 2*t*t : -1+(4-2*t)*t;
      _camera.position.lerpVectors(start, end, e);
      _controls.target.lerp(target, 0.06);
      if (t < 1) requestAnimationFrame(step);
    };
    step();
  }

  function onMouseMove(e) {
    const r = _renderer.domElement.getBoundingClientRect();
    _mouse.x =  (e.clientX - r.left) / r.width  * 2 - 1;
    _mouse.y = -(e.clientY - r.top)  / r.height * 2 + 1;
  }

  function onClick(e) {
    // Only fire click if we didn't drag (OrbitControls moves camera on drag)
    if (_lastHoveredIdx >= 0 && _dotnet) {
      _dotnet.invokeMethodAsync('OnClick', _points[_lastHoveredIdx]?.key ?? '');
    }
  }

  let _hoverMs = 0;
  function checkHover() {
    if (!_pointsMesh || !_raycaster || Date.now() - _hoverMs < 40) return;
    _hoverMs = Date.now();
    _raycaster.setFromCamera(_mouse, _camera);
    const hits = _raycaster.intersectObject(_pointsMesh);
    const idx  = hits.length > 0 ? hits[0].index : -1;
    if (idx !== _lastHoveredIdx) {
      _lastHoveredIdx = idx;
      _pointsMesh.material.uniforms.hovered.value = idx >= 0 ? idx : -1;
      if (_dotnet) _dotnet.invokeMethodAsync('OnHover', idx >= 0 ? (_points[idx]?.key ?? '') : '');
    }
  }

  function onResize() {
    if (!_container || !_renderer) return;
    const W = _container.clientWidth, H = _container.clientHeight;
    _camera.aspect = W / H; _camera.updateProjectionMatrix();
    _renderer.setSize(W, H);
  }

  function animate() {
    _animId = requestAnimationFrame(animate);
    _controls.update();
    checkHover();
    _renderer.render(_scene, _camera);
  }

  return { init, setQuery, clearQuery };
})();
