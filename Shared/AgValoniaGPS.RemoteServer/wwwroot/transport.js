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

    const TYPE = { SCENE: 1, TICK: 2, COVERAGE_INIT: 3, COVERAGE_CELLS: 4, STATUS: 5, CONTROL_STATE: 6, HELLO: 7, CONFIG: 8, PROFILES: 9, WIZARD: 10, NTRIP_PROFILES: 11, FIELD_OPS: 12, AGSHARE: 13, APP_INFO: 14, FIELD_TOOLS: 15, RECORDED_PATH: 16, BOUNDARY: 17 };
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
          const bc = i32(); const boundaries = new Array(bc); const boundaryInner = new Array(bc);
          for (let k = 0; k < bc; k++) { boundaryInner[k] = !!u8(); boundaries[k] = pts(); }
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
          const flagCount = i32(); const flags = new Array(flagCount);
          for (let k = 0; k < flagCount; k++) flags[k] = { e: f32(), n: f32(), color: str(), name: str() };
          const imagery = u8()
            ? { minE: f64(), minN: f64(), maxE: f64(), maxN: f64(), version: i64() }
            : null;
          const tlc = i32(); const trackList = new Array(tlc);
          for (let k = 0; k < tlc; k++)
            trackList[k] = { index: i32(), name: str(), type: str(), active: !!u8(), visible: !!u8() };
          handlers.onScene && handlers.onScene({
            version, originLat, originLon, fieldName, hasField, boundaries, boundaryInner, tracks,
            headland, guidanceLine, toolSections, uTurnPath, nextTrack, flags, imagery, trackList,
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
          const headlandDist = f32(), headlandWarn = !!u8();
          const steerAngleError = f32();
          // Diagnostic-chart scalars (Tools panel charts).
          const chartSetSteer = f32(), chartActualSteer = f32(), chartPwm = f32(), chartImuHeading = f32();
          const hitchE = f64(), hitchN = f64();
          handlers.onTick && handlers.onTick({
            sceneVersion, pose, fix, sections, crossTrackError, guidanceActive, lineLabel,
            activeTrackName: atn.length ? atn : null, tool, op, roll, tools,
            headlandDist, headlandWarn, steerAngleError,
            chartSetSteer, chartActualSteer, chartPwm, chartImuHeading,
            hitchE, hitchN,
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
          const actualSteerAngle = f32(), sensorPercent = f32(), setSteerAngle = f32(),
                freeDriveAngle = f32(), steerFreeDrive = !!u8();
          const swCollecting = !!u8(), swSamples = i32(), swMean = f32(), swMedian = f32(),
                swStdDev = f32(), swOffsetDeg = f32(), swConfidence = f32(), swValid = !!u8();
          // Network IO panel (append-only).
          const gpsIp = str(), moduleSubnet = str(), hostIps = str();
          const ntripConnected = !!u8(), ntripStatus = str(), ntripBytes = f64(), ntripTestStatus = str();
          const simPanelVisible = !!u8();
          const driftEasting = f32(), driftNorthing = f32();
          handlers.onStatusBar && handlers.onStatusBar({
            fixQuality, fixText, age, sats, isMetric,
            gpsOk, imuOk, autoSteerOk, machineOk, imuIp, autoSteerIp, machineIp,
            gpsConf, imuConf, autoSteerConf, machineConf, jobName, workedAreaSqM,
            lat, lon, altitude, hdop,
            simEnabled, simSpeedKph, simSteerAngle, sim10x,
            actualSteerAngle, sensorPercent, setSteerAngle, freeDriveAngle, steerFreeDrive,
            swCollecting, swSamples, swMean, swMedian, swStdDev, swOffsetDeg, swConfidence, swValid,
            gpsIp, moduleSubnet, hostIps, ntripConnected, ntripStatus, ntripBytes, ntripTestStatus,
            simPanelVisible, driftEasting, driftNorthing,
          });
          break;
        }
        case TYPE.NTRIP_PROFILES: {
          const pc = i32(); const profiles = new Array(pc);
          for (let k = 0; k < pc; k++) {
            const id = str(), name = str(), casterHost = str(), casterPort = i32(), mountPoint = str();
            const username = str(), password = str(), autoConnect = !!u8(), isDefault = !!u8();
            const ac = i32(); const associatedFields = new Array(ac);
            for (let j = 0; j < ac; j++) associatedFields[j] = str();
            profiles[k] = { id, name, casterHost, casterPort, mountPoint, username, password, autoConnect, isDefault, associatedFields };
          }
          const af = i32(); const availableFields = new Array(af);
          for (let k = 0; k < af; k++) availableFields[k] = str();
          handlers.onNtripProfiles && handlers.onNtripProfiles({ profiles, availableFields });
          break;
        }
        case TYPE.FIELD_OPS: {
          const fc = i32(); const fields = new Array(fc);
          for (let k = 0; k < fc; k++) fields[k] = { name: str(), hasDistance: !!u8(), distanceKm: f64(), areaHa: f64() };
          const jc = i32(); const jobs = new Array(jc);
          for (let k = 0; k < jc; k++) jobs[k] = { fieldName: str(), taskName: str(), workType: str(), status: i32(), lastOpened: str(), notes: str() };
          const wc = i32(); const workTypes = new Array(wc); for (let k = 0; k < wc; k++) workTypes[k] = str();
          const ic = i32(); const isoFiles = new Array(ic); for (let k = 0; k < ic; k++) isoFiles[k] = str();
          const kc = i32(); const kmlFiles = new Array(kc); for (let k = 0; k < kc; k++) kmlFiles[k] = str();
          const activeField = str();
          handlers.onFieldOps && handlers.onFieldOps({ fields, jobs, workTypes, isoFiles, kmlFiles, activeField });
          break;
        }
        case TYPE.AGSHARE: {
          const serverUrl = str(), apiKey = str(), enabled = !!u8(), status = str(), busy = !!u8();
          const lc = i32(); const localFields = new Array(lc);
          for (let k = 0; k < lc; k++) localFields[k] = { name: str(), hasBoundary: !!u8() };
          const cc = i32(); const cloudFields = new Array(cc);
          for (let k = 0; k < cc; k++) cloudFields[k] = { id: str(), name: str(), areaHa: f64() };
          handlers.onAgShare && handlers.onAgShare({ serverUrl, apiKey, enabled, status, busy, localFields, cloudFields });
          break;
        }
        case TYPE.APP_INFO: {
          const version = str(), gitHash = str(), currentLanguage = str();
          const lc = i32(); const languages = new Array(lc);
          for (let k = 0; k < lc; k++) languages[k] = { code: str(), name: str() };
          const dc = i32(); const directories = new Array(dc);
          for (let k = 0; k < dc; k++) directories[k] = { name: str(), path: str(), exists: !!u8() };
          const hc = i32(); const hotkeys = new Array(hc);
          for (let k = 0; k < hc; k++) hotkeys[k] = { action: str(), key: str(), label: str() };
          const gc = i32(); const logs = new Array(gc);
          for (let k = 0; k < gc; k++) logs[k] = { time: str(), level: i32(), message: str() };
          const bugReportStatus = str();
          handlers.onAppInfo && handlers.onAppInfo({ version, gitHash, currentLanguage, languages, directories, hotkeys, logs, bugReportStatus });
          break;
        }
        case TYPE.FIELD_TOOLS: {
          const ic = i32(); const importFields = new Array(ic);
          for (let k = 0; k < ic; k++) importFields[k] = str();
          handlers.onFieldTools && handlers.onFieldTools({ importFields });
          break;
        }
        case TYPE.RECORDED_PATH: {
          const rc = i32(); const recFiles = new Array(rc);
          for (let k = 0; k < rc; k++) recFiles[k] = str();
          const isRecording = !!u8(), isPlaying = !!u8(), hasUnsaved = !!u8();
          const recordedPathInfo = str(), resumeModeLabel = str(), recordedPathName = str();
          const pc = i32(); const recordingPoints = new Array(pc);
          for (let k = 0; k < pc; k++) recordingPoints[k] = f32();
          handlers.onRecordedPath && handlers.onRecordedPath({ recFiles, isRecording, isPlaying, hasUnsaved, recordedPathInfo, resumeModeLabel, recordedPathName, recordingPoints });
          break;
        }
        case TYPE.BOUNDARY: {
          const ic = i32(); const items = new Array(ic);
          for (let k = 0; k < ic; k++) items[k] = { index: i32(), boundaryType: str(), areaDisplay: str(), driveThru: !!u8(), hard: !!u8() };
          const selectedIndex = i32(), playerVisible = !!u8(), isRecording = !!u8(), isPaused = !!u8();
          const pointCount = i32(), areaHa = f64(), offsetCm = f64();
          const drawRightSide = !!u8(), drawAtPivot = !!u8(), sectionControlOn = !!u8();
          const pc = i32(); const recordingPoints = new Array(pc);
          for (let k = 0; k < pc; k++) recordingPoints[k] = f32();
          handlers.onBoundary && handlers.onBoundary({ items, selectedIndex, playerVisible, isRecording, isPaused, pointCount, areaHa, offsetCm, drawRightSide, drawAtPivot, sectionControlOn, recordingPoints });
          break;
        }
        case TYPE.HELLO: {
          handlers.onHello && handlers.onHello(str());
          break;
        }
        case TYPE.CONFIG: {
          const vehicle = {
            name: str(), type: i32(), hitchType: i32(), hitchLength: f64(), wheelbase: f64(),
            trackWidth: f64(), antennaPivot: f64(), antennaHeight: f64(), antennaOffset: f64(),
          };
          const gps = {
            isDualGps: !!u8(), dualHeadingOffset: f64(), dualReverseDistance: f64(),
            autoDualFix: !!u8(), dualSwitchSpeed: f64(), minGpsStep: f64(), fixToFixDistance: f64(),
            headingFusionWeight: f64(), reverseDetection: !!u8(), rtkLostAlarm: !!u8(), rtkLostAction: i32(),
          };
          const roll = { rollZero: f64(), rollFilter: f64(), isRollInvert: !!u8() };
          const rdF64 = () => { const n = i32(), a = new Array(n); for (let k = 0; k < n; k++) a[k] = f64(); return a; };
          const rdI32 = () => { const n = i32(), a = new Array(n); for (let k = 0; k < n; k++) a[k] = i32(); return a; };
          const tool = {
            type: i32(), hitchType: i32(), hitchLength: f64(), trailingHitchLength: f64(),
            tankTrailingHitchLength: f64(), length: f64(), lookAheadOn: f64(), lookAheadOff: f64(),
            turnOffDelay: f64(), offset: f64(), overlap: f64(), trailingToolToPivotLength: f64(),
            isSectionsNotZones: !!u8(), numSections: i32(), defaultSectionWidth: f64(),
            sectionWidths: rdF64(), zones: i32(), zoneRanges: rdI32(),
            isMultiColoredSections: !!u8(), sectionColors: rdI32(), singleCoverageColor: i32(),
            isSectionOffWhenOut: !!u8(), isHeadlandSectionControl: !!u8(), minCoverage: i32(),
            slowSpeedCutoff: f64(), coverageMargin: f64(),
            isWorkSwitchEnabled: !!u8(), isWorkSwitchActiveLow: !!u8(), isWorkSwitchManualSections: !!u8(),
            isSteerSwitchEnabled: !!u8(), isSteerSwitchManualSections: !!u8(), totalWidth: f64(),
          };
          const uturn = { style: i32(), extension: f64(), smoothing: i32(), radius: f64(), distanceFromBoundary: f64() };
          const tram = { passes: i32(), display: !!u8(), line: i32() };
          const machine = {
            hydraulicLiftEnabled: !!u8(), raiseTime: i32(), lookAhead: f64(), lowerTime: i32(), invertRelay: !!u8(),
            user1: i32(), user2: i32(), user3: i32(), user4: i32(), pinAssignments: rdI32(),
          };
          const display = {
            gridVisible: !!u8(), fieldTextureVisible: !!u8(), fieldTextureMoveable: !!u8(), svennArrowVisible: !!u8(),
            headlandDistanceVisible: !!u8(), lineSmoothEnabled: !!u8(), autoDayNight: !!u8(), hardwareMessagesEnabled: !!u8(),
            extraGuidelines: !!u8(), extraGuidelinesCount: i32(), resolutionLabel: str(),
            uTurnButtonVisible: !!u8(), lateralButtonVisible: !!u8(),
            autoSteerSound: !!u8(), uTurnSound: !!u8(), hydraulicSound: !!u8(), sectionsSound: !!u8(),
            keyboardEnabled: !!u8(), startFullscreen: !!u8(), elevationLogEnabled: !!u8(),
          };
          // AutoSteer config — full 9-tab surface (positional, matches WireCodec).
          const autosteer = {
            steerResponseHold: f64(), integralGain: f64(), isStanleyMode: !!u8(),
            stanleyAggressiveness: f64(), stanleyOvershootReduction: f64(),
            wasOffset: i32(), countsPerDegree: f64(), ackermann: i32(), maxSteerAngle: i32(),
            deadzoneHeading: f64(), deadzoneDelay: i32(), speedFactor: f64(), acquireFactor: f64(),
            proportionalGain: i32(), maxPwm: i32(), minPwm: i32(),
            turnSensorEnabled: !!u8(), pressureSensorEnabled: !!u8(), currentSensorEnabled: !!u8(),
            turnSensorCounts: i32(), pressureTripPoint: i32(), currentTripPoint: i32(),
            danfossEnabled: !!u8(), invertWas: !!u8(), invertMotor: !!u8(), invertRelays: !!u8(),
            motorDriver: i32(), adConverter: i32(), imuAxisSwap: i32(), externalEnable: i32(),
            uTurnCompensation: f64(), sideHillCompensation: f64(), steerInReverse: !!u8(),
            manualTurnsEnabled: !!u8(), manualTurnsSpeed: f64(), minSteerSpeed: f64(), maxSteerSpeed: f64(),
            lineWidth: i32(), nudgeDistance: i32(), nextGuidanceTime: f64(), cmPerPixel: i32(),
            lightbarEnabled: !!u8(), steerBarEnabled: !!u8(), guidanceBarOn: !!u8(),
          };
          handlers.onConfig && handlers.onConfig({ vehicle, gps, roll, tool, uturn, tram, machine, display, autosteer });
          break;
        }
        case TYPE.WIZARD: {
          const stepIndex = i32(), totalSteps = i32(), stepKind = str(), title = str(), description = str();
          const canBack = !!u8(), canNext = !!u8(), canSkip = !!u8(), isLast = !!u8(), validation = str();
          const statusWas = f32(), statusRoll = f32(), statusGps = str(), statusSpeed = f32(),
                statusPwm = i32(), statusConnected = !!u8();
          const hardwareLevel = i32();
          const liveAngle = f32(), liveRoll = f32(), liveError = f32();
          const testPhase = str(), testResult = str(), testProgress = f32(), testActive = !!u8();
          const rtkFixed = !!u8(), fixLabel = str(), diameter = f32();
          handlers.onWizard && handlers.onWizard({
            stepIndex, totalSteps, stepKind, title, description,
            canBack, canNext, canSkip, isLast, validation,
            statusWas, statusRoll, statusGps, statusSpeed, statusPwm, statusConnected,
            hardwareLevel, liveAngle, liveRoll, liveError,
            testPhase, testResult, testProgress, testActive, rtkFixed, fixLabel, diameter,
          });
          break;
        }
        case TYPE.PROFILES: {
          const activeVehicle = str(), activeTool = str();
          const rdList = () => { const n = i32(), out = new Array(n); for (let k = 0; k < n; k++) out[k] = { name: str(), preview: str() }; return out; };
          const vehicles = rdList(), tools = rdList();
          handlers.onProfiles && handlers.onProfiles({ activeVehicle, activeTool, vehicles, tools });
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
