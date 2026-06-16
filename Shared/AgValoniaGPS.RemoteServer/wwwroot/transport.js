// Transport adapter — the ONLY file that knows the wire protocol.
//
// Binary WebSocket. Each frame is [u8 type][payload], little-endian, matching
// the server-side WireCodec. The renderer never sees this: it only gets the
// { onScene, onTick, onCoverageInit, onCoverageCells, onStatus } interface and
// plain JS objects (identical shapes to the old SignalR/JSON path), so swapping
// the wire here left app.js untouched. That decoupling is the whole point.

window.RemoteTransport = {
  /**
   * @param {{ onScene?: (scene:object)=>void,
   *           onTick?:  (tick:object)=>void,
   *           onCoverageInit?: (init:object)=>void,
   *           onCoverageCells?: (cells:object)=>void,
   *           onStatusBar?:(status:object)=>void,
   *           onHello?:(clientId:string)=>void,
   *           onControlState?:(state:object)=>void,
   *           onStatus?:(state:string)=>void }} handlers
   */
  create(handlers) {
    const status = s => handlers.onStatus && handlers.onStatus(s);
    const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const url = `${proto}//${location.host}/ws`;
    let ws = null, stopped = false;

    const TYPE = { SCENE: 1, TICK: 2, COVERAGE_INIT: 3, COVERAGE_CELLS: 4, STATUS: 5, CONTROL_STATE: 6, HELLO: 7 };
    const td = new TextDecoder();

    function decode(buffer) {
      const dv = new DataView(buffer);
      let o = 0;
      const u8 = () => dv.getUint8(o++);
      const i32 = () => { const v = dv.getInt32(o, true); o += 4; return v; };
      const i64 = () => { const v = Number(dv.getBigInt64(o, true)); o += 8; return v; };
      const f32 = () => { const v = dv.getFloat32(o, true); o += 4; return v; };
      const f64 = () => { const v = dv.getFloat64(o, true); o += 8; return v; };
      const str = () => { const n = i32(); const s = td.decode(new Uint8Array(buffer, o, n)); o += n; return s; };
      const pts = () => { const n = i32(); const a = new Array(n); for (let k = 0; k < n; k++) a[k] = { e: f32(), n: f32() }; return a; };
      const optPts = () => (u8() ? pts() : null);

      switch (u8()) {
        case TYPE.SCENE: {
          const version = i64(), originLat = f64(), originLon = f64();
          const hasField = !!u8(), fieldName = str();
          const bc = i32(); const boundaries = new Array(bc);
          for (let k = 0; k < bc; k++) boundaries[k] = pts();
          const tc = i32(); const tracks = new Array(tc);
          for (let k = 0; k < tc; k++) {
            const id = str(), name = str(), type = i32(), points = pts();
            tracks[k] = { id, name, type, points };
          }
          const headland = optPts(), guidanceLine = optPts();
          const sc = i32(); const toolSections = new Array(sc);
          for (let k = 0; k < sc; k++) toolSections[k] = { left: f32(), right: f32() };
          const uTurnPath = optPts();
          const nextTrack = optPts();
          const imagery = u8()
            ? { minE: f64(), minN: f64(), maxE: f64(), maxN: f64(), version: i64() }
            : null;
          handlers.onScene && handlers.onScene({
            version, originLat, originLon, fieldName, hasField, boundaries, tracks,
            headland, guidanceLine, toolSections, uTurnPath, nextTrack, imagery,
          });
          break;
        }
        case TYPE.TICK: {
          const sceneVersion = i64();
          const pose = { e: f64(), n: f64(), heading: f32(), speed: f32() };
          const fix = u8();
          const sn = i32(); const sections = new Array(sn);
          for (let k = 0; k < sn; k++) sections[k] = u8(); // ColorCode 0..5 (see SECTION_COLORS)
          const crossTrackError = f32(), guidanceActive = !!u8(), lineLabel = str();
          const atn = str();
          const tool = { e: f64(), n: f64(), heading: f32(), ready: !!u8() };
          // Operational state for the right-nav toolbar.
          const op = {
            autoSteer: !!u8(), autoSteerAvail: !!u8(), contour: !!u8(),
            sectionAuto: !!u8(), sectionManual: !!u8(), youturn: !!u8(),
            turnLeft: !!u8(), distToTrigger: f32(), trackClosed: !!u8(),
          };
          const roll = f32();
          // Bottom-nav field-tools (Phase 8).
          const tools = {
            headlandOn: !!u8(), sectionInHeadland: !!u8(), autoTrack: !!u8(),
            skipRows: u8(), skipRowsOn: !!u8(), tramMode: u8(),
          };
          handlers.onTick && handlers.onTick({
            sceneVersion, pose, fix, sections, crossTrackError, guidanceActive, lineLabel,
            activeTrackName: atn.length ? atn : null, tool, op, roll, tools,
          });
          break;
        }
        case TYPE.STATUS: {
          const fixQuality = i32(), fixText = str(), age = f32(), sats = i32();
          const isMetric = !!u8();
          const gpsOk = !!u8(), imuOk = !!u8(), autoSteerOk = !!u8(), machineOk = !!u8();
          const imuIp = str(), autoSteerIp = str(), machineIp = str();
          const gpsConf = !!u8(), imuConf = !!u8(), autoSteerConf = !!u8(), machineConf = !!u8();
          const jobName = str(), workedAreaSqM = f64();
          const lat = f64(), lon = f64(), altitude = f32(), hdop = f32();
          const simEnabled = !!u8(), simSpeedKph = f32(), simSteerAngle = f32(), sim10x = !!u8();
          handlers.onStatusBar && handlers.onStatusBar({
            fixQuality, fixText, age, sats, isMetric,
            gpsOk, imuOk, autoSteerOk, machineOk, imuIp, autoSteerIp, machineIp,
            gpsConf, imuConf, autoSteerConf, machineConf, jobName, workedAreaSqM,
            lat, lon, altitude, hdop,
            simEnabled, simSpeedKph, simSteerAngle, sim10x,
          });
          break;
        }
        case TYPE.HELLO: {
          handlers.onHello && handlers.onHello(str());
          break;
        }
        case TYPE.CONTROL_STATE: {
          const held = !!u8(), holderId = str(), holderName = str();
          handlers.onControlState && handlers.onControlState({ held, holderId, holderName });
          break;
        }
        case TYPE.COVERAGE_INIT: {
          handlers.onCoverageInit && handlers.onCoverageInit({
            cellSize: f64(), originE: f64(), originN: f64(), width: i32(), height: i32(),
          });
          break;
        }
        case TYPE.COVERAGE_CELLS: {
          const n = i32();
          // Flat [cellX, cellY, packedRgb, ...] int32 triples. slice() to get a
          // 4-aligned copy (the payload starts at byte 5, so a direct typed-array
          // view over `buffer` would throw on the alignment requirement).
          const cells = new Int32Array(buffer.slice(o, o + n * 4));
          handlers.onCoverageCells && handlers.onCoverageCells({ cells });
          break;
        }
      }
    }

    function connect() {
      status('connecting…');
      ws = new WebSocket(url);
      ws.binaryType = 'arraybuffer';
      ws.onopen = () => status('connected');
      ws.onmessage = e => { try { decode(e.data); } catch (err) { /* drop a malformed frame */ } };
      ws.onerror = () => status('error');
      ws.onclose = () => { status('disconnected'); if (!stopped) setTimeout(connect, 1000); };
    }

    return {
      start() { stopped = false; connect(); },
      stop() { stopped = true; if (ws) ws.close(); },
      // Client→host command: a short text frame carrying a command id.
      send(cmd) { if (ws && ws.readyState === WebSocket.OPEN) ws.send(cmd); },
    };
  },
};
