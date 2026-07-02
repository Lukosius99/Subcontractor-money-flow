(() => {
  "use strict";

  const colors = {
    hub: "#d9e8ff",
    monthly: "#4e9cff",
    contracts: "#3fd18c",
    automation: "#a67cff",
    bridge: "#ff9b54",
    database: "#6ed5d0",
    ui: "#86a8ff",
    external: "#8792a8"
  };

  const groups = {
    hub: "Bendras vaizdas",
    monthly: "Mėnesinis aktavimas",
    contracts: "Sutartinės vertės",
    automation: "Power Automate Cloud Flow",
    bridge: "PAD ir lokali API",
    database: "SQLite duomenų bazė",
    ui: "Web UI",
    external: "Išorinės sistemos"
  };

  const node = (id, label, group, description, options = {}) => ({
    id,
    label,
    group,
    description,
    status: options.status || "process",
    kind: options.kind || "process",
    badges: options.badges || [],
    technical: options.technical || "",
    tags: options.tags || [group],
    val: options.val || (options.kind === "cluster" ? 13 : options.kind === "technical" ? 4 : 7),
    ...options.position
  });

  const nodes = [
    node("hub", "Subrangos pinigų srautas", "hub", "Interaktyvi schema parodo, kaip mėnesiniai aktavimo duomenys ir sutartinės vertės keliauja nuo pirminių šaltinių iki lokalios web programos, SQLite duomenų bazės ir naudotojo sąsajos.", { kind: "cluster", badges: ["Bendras vaizdas"], tags: ["monthly", "contracts", "bridge", "inside"], val: 22, position: { fx: 0, fy: 0, fz: 0 } }),

    node("monthly-hub", "Mėnesinis aktavimas", "monthly", "Faktinės mėnesio subrangovų sąskaitos ir užsakovo vertės iš Aktavimo Excel.", { kind: "cluster", badges: ["Excel", "JSON"], tags: ["monthly"], val: 14 }),
    node("contracts-hub", "Sutartinės vertės", "contracts", "Sutartinės sumos sudaro bazinį palyginimo lygį, kurį vėliau papildo mėnesinis faktas.", { kind: "cluster", badges: ["Baseline", "JSON"], tags: ["contracts"], val: 14 }),
    node("sharepoint-hub", "DB SharePoint", "external", "Tarpinė Excel ir JSON failų saugykla tarp Cloud Flow ir PAD.", { kind: "cluster", badges: ["Saugykla"], tags: ["monthly", "contracts", "bridge"], val: 12 }),
    node("pad-hub", "PAD importas", "bridge", "PAD importo tiltas paima naujausią JSON iš DB SharePoint, laikinai išsaugo jį kompiuteryje, perskaito ir HTTP POST užklausa perduoda lokaliai API.", { kind: "cluster", badges: ["Automatinis žingsnis"], technical: "<ul><li>Mėnesinis temp failas: <code>latest-monthly-flow.json</code>; klaida: <code>MonthlyFlowImportFailed</code>.</li><li>Sutartinis temp failas: <code>latest-contracted.json</code>; klaida: <code>ContractedImportFailed</code>.</li><li>Gavęs statusą 200 PAD ištrina laikiną failą, kitu atveju išsaugo klaidos informaciją.</li></ul>", tags: ["monthly", "contracts", "bridge"], val: 14 }),
    node("api-hub", "Lokali API", "bridge", "ASP.NET Core API priima mėnesinius ir sutartinius JSON per du atskirus endpointus.", { kind: "cluster", badges: ["API", "Patvirtinta kode"], status: "code", tags: ["monthly", "contracts", "bridge", "inside"], val: 14 }),
    node("db-hub", "SQLite duomenų bazė", "database", "Importai, projektai, sutartys, objektų vertės, perspėjimai ir susiejimai saugomi lokalioje SQLite bazėje.", { kind: "cluster", badges: ["SQLite", "Patvirtinta kode"], status: "code", tags: ["inside", "monthly", "contracts"], val: 14 }),
    node("ui-hub", "Web UI", "ui", "Projektų sąrašas ir projekto detalė pateikia sutartinių verčių, mėnesinio srauto, kliento verčių ir būsenų suvestines.", { kind: "cluster", badges: ["Patvirtinta kode"], status: "code", tags: ["inside", "monthly", "contracts"], val: 13 }),

    node("monthly-excel", "Aktavimo Excel PADS SharePoint", "monthly", "Administravimo skyriaus inžinierius pildo Aktavimo Excel failą PADS SharePoint aplinkoje.", { badges: ["Excel", "Rankinis žingsnis"], technical: "<ul><li>Pirminis mėnesinio srauto šaltinis.</li><li>Duomenys skaitomi iš <code>subranga</code> ir <code>SMD</code> lapų.</li></ul>", tags: ["monthly"] }),
    node("monthly-cloud", "Cloud Flow: mėnesinio JSON eksportas", "automation", "Power Automate Cloud Flow paleidžia Office Script ir paruošia mėnesinį JSON eksportą.", { badges: ["Automatinis žingsnis", "Cloud Flow"], technical: "Inicializuoja metus, mėnesio numerį ir lietuvišką mėnesio pavadinimą, suranda failą, paleidžia scriptą, sukuria eksportą ir atnaujina latest failą.", tags: ["monthly"] }),
    node("monthly-script", "Office Script: subranga + SMD", "automation", "Scriptas skaito du Excel lapus ir grąžina vieną JSON objektą.", { badges: ["Automatinis žingsnis", "JSON"], technical: "<ul><li><code>schemaVersion: 1.4</code></li><li><code>sourceSystem: SharePointMonthlyFlowExcel</code></li><li>Grąžina metus, mėnesį, lapų vardus, eksporto laiką, eilučių skaičius, <code>rows</code> ir <code>warnings</code>.</li></ul>", tags: ["monthly"] }),
    node("subranga-sheet", "subranga lapas", "monthly", "Subrangovų faktinių sąskaitų eilutės. Reikšmės tęsiamos žemyn, eilutės be objekto kodo praleidžiamos.", { kind: "technical", badges: ["Excel", "subcontractorInvoice"], technical: "<ul><li>B = subrangovas</li><li>C = objekto pavadinimas</li><li>E = suma be PVM</li><li>H = projekto / objekto kodas</li><li>I = atsakingas</li><li>J = inžinierius</li></ul>", tags: ["monthly"] }),
    node("smd-sheet", "SMD lapas", "monthly", "Užsakovo arba kliento vertės. SMD B stulpelyje yra klientas, ne subrangovas.", { kind: "technical", badges: ["Excel", "clientValue"], technical: "<ul><li>B = užsakovas / klientas</li><li>C = objekto pavadinimas</li><li>E = suma be PVM, D = atsarginė suma</li><li>H = objekto kodas</li><li>I = atsakingas</li><li>J = inžinierius</li><li><code>subcontractorName</code> turi būti tuščias.</li></ul>", tags: ["monthly"] }),
    node("monthly-json", "latest-monthly-flow.json", "monthly", "DB SharePoint saugomas naujausias mėnesinio srauto JSON, kurį vėliau paima PAD.", { badges: ["JSON", "Automatinis žingsnis"], technical: "Cloud Flow taip pat gali saugoti datuotą eksporto kopiją. Latest failas atnaujinamas ištrinant seną ir sukuriant naują.", tags: ["monthly", "bridge"] }),
    node("monthly-api", "POST /api/imports/monthly-flow", "bridge", "Mėnesinio srauto API endpointas priima JSON kūną arba multipart failą.", { status: "code", badges: ["API", "Patvirtinta kode"], technical: "<code>http://localhost:5000/api/imports/monthly-flow?sourceFileName=latest-monthly-flow.json</code><ul><li>Palaiko schemas 1.3 ir 1.4.</li><li>1.4 schemai būtinas <code>sourceSheet</code>.</li><li>Dubliuotas visas importas aptinkamas pagal turinio hash.</li></ul>", tags: ["monthly", "bridge", "inside"] }),

    node("dvs", "Subrangos dokumentas DVS / DocLogix", "external", "PADS inžinierius įkelia subrangos dokumentą į vidinę DVS / DocLogix sistemą.", { badges: ["Rankinis žingsnis"], tags: ["contracts"] }),
    node("dynamics", "Dynamics", "external", "Duomenys iš DVS / DocLogix perduodami į Dynamics.", { badges: ["Išorinė sistema"], tags: ["contracts"] }),
    node("sap-bo", "SAP Business Objects", "external", "Iš Dynamics duomenys pasiekia SAP Business Objects eksporto grandį.", { badges: ["Išorinė sistema"], tags: ["contracts"] }),
    node("mailbox", "DB mailbox", "external", "SAP BO siunčia arba eksportuoja failą į DB pašto dėžutę.", { badges: ["Išorinė sistema"], tags: ["contracts"] }),
    node("contract-cloud", "Cloud Flow: sutartinių failų gavimas", "automation", "Cloud Flow stebi DB mailbox ir išsaugo gautą Excel priedą DB SharePoint.", { badges: ["Automatinis žingsnis", "Excel"], tags: ["contracts"] }),
    node("contract-script", "Office Script: sutartinių verčių eksportas", "automation", "Numatytas scriptas turi sukurti sutarčių JSON pagal kontraktinių verčių Excel stulpelius.", { badges: ["Excel", "Reikia patikrinti"], status: "check", technical: "<ul><li>A = projekto kodas</li><li>B = projekto pavadinimas</li><li>E = subrangovo pavadinimas</li><li>F = sutartinė suma</li><li>J = objekto spausdinamas / padalinio objekto kodas</li><li>Eilutės be J praleidžiamos, dublikatai tame pačiame projekte ir objekte sumuojami.</li></ul>", tags: ["contracts"] }),
    node("contract-json", "latest-contracted.json", "contracts", "DB SharePoint aplinkoje saugomas naujausias sutartinių verčių JSON.", { badges: ["JSON", "Automatinis žingsnis"], tags: ["contracts", "bridge"] }),
    node("contract-api", "POST /api/imports/contracts", "bridge", "Faktinis API endpointas priima ContractImportRequest, o ne mėnesinio srauto JSON.", { status: "code", badges: ["API", "Patvirtinta kode"], technical: "<code>http://localhost:5000/api/imports/contracts</code><ul><li>Būtinas bent vienas masyvas: <code>rows</code> arba <code>projectValueRows</code>.</li><li>Sutarčiai būtini: <code>projectCode</code>, <code>objectNumber</code>, <code>subcontractorName</code>, <code>contractedAmount</code>.</li><li>Projekto vertei būtini: <code>projectCode</code>, <code>objectNumber</code>, <code>projectValueAmount</code>.</li></ul>", tags: ["contracts", "bridge", "inside"] }),

    node("monthly-validation", "Mėnesinio JSON validavimas", "bridge", "MonthlyFlowImportService tikrina schemos versiją, metus, mėnesį, eilučių masyvą ir pagrindinius laukus.", { kind: "technical", status: "code", badges: ["DTO", "Patvirtinta kode"], technical: "Netinkamos eilutės dažniausiai praleidžiamos su perspėjimu. SMD atpažįstamas pagal <code>sourceSheet</code>, kliento vardas saugomas atskirai.", tags: ["monthly", "inside"] }),
    node("contract-validation", "Sutarčių DTO ir validavimas", "bridge", "ContractImportRequest atskiria sutarties eilutes nuo projektų verčių eilučių.", { kind: "technical", status: "code", badges: ["DTO", "Patvirtinta kode"], technical: "Validavimas normalizuoja privalomus laukus, skaičiuoja perspėjimus ir suformuoja loginius eilučių raktus.", tags: ["contracts", "inside"] }),
    node("normalization", "Subrangovų normalizavimas", "database", "Subrangovų pavadinimai normalizuojami ir registruojami kartu su aliasais, kad mėnesio ir sutarčių šaltinius būtų galima palyginti.", { kind: "technical", status: "code", badges: ["Atitikmenys", "Patvirtinta kode"], technical: "Naudojami <code>Subcontractor</code>, <code>SubcontractorAlias</code> ir rankiniai <code>ManualContractLink</code> susiejimai.", tags: ["inside", "monthly", "contracts"] }),
    node("project-upsert", "Projektų ir sutarčių upsert", "database", "Sutartinių duomenų importas sukuria arba atnaujina projektų momentines kopijas, sutartis ir objektų vertes.", { kind: "technical", status: "code", badges: ["Upsert", "Patvirtinta kode"], technical: "Naujausiame kontraktų importe nebepasirodžiusios sutartys ir projektai pažymimi neaktyviais, o ne tyliai ištrinami.", tags: ["inside", "contracts"] }),
    node("monthly-aggregation", "Mėnesinių sumų agregavimas", "database", "Vienodą loginį raktą turinčios mėnesio eilutės suglaudinamos ir jų sumos sudedamos.", { kind: "technical", status: "code", badges: ["Agregavimas", "Patvirtinta kode"], technical: "Raktas apima mėnesio ir projekto, objekto, subrangovo arba kliento dimensijas. Tai apsaugo nuo unikalumo konflikto ir išlaiko bendrą sumą.", tags: ["inside", "monthly"] }),
    node("ignored-rows", "Ignoruotos mėnesio eilutės", "database", "Naudotojas gali neįtraukti konkrečios mėnesio eilutės į sumas ir vėliau ją atkurti.", { kind: "technical", status: "code", badges: ["Patvirtinta kode"], technical: "Saugomi <code>IsExcludedFromTotals</code>, laikas, priežastis ir pakeitimą atlikęs naudotojas. Tam skirti exclude ir restore endpointai.", tags: ["inside", "monthly"] }),
    node("db-tables", "Pagrindinės SQLite lentelės", "database", "Duomenų modelis atskiria importo auditą, mėnesines eilutes, sutartis, projektų objektų vertes ir rankinius susiejimus.", { kind: "technical", status: "code", badges: ["SQLite", "Patvirtinta kode"], technical: "<ul><li><code>ImportBatches</code>, <code>ImportWarnings</code></li><li><code>MonthlyFlowRows</code>, <code>Projects</code></li><li><code>SubcontractorContracts</code>, <code>ProjectObjectValues</code></li><li><code>Subcontractors</code>, <code>SubcontractorAliases</code></li><li><code>ManualContractLinks</code>, <code>ManualObjectAssignments</code></li></ul>", tags: ["inside"] }),
    node("project-list", "UI: projektų sąrašas", "ui", "Projektų registras rodo aktyvius projektus, finansines suvestines, filtrus ir būsenų pjūvius.", { kind: "technical", status: "code", badges: ["Patvirtinta kode"], technical: "Sąrašas duomenis gauna iš <code>GET /api/projects</code> ir rodo naujausio importo būseną.", tags: ["inside", "monthly", "contracts"] }),
    node("project-detail", "UI: projekto ir objektų detalė", "ui", "Detalės puslapyje lyginamos sutartinės sumos, mėnesinis faktas, kliento vertės, subrangovai ir objekto būsenos.", { kind: "technical", status: "code", badges: ["Patvirtinta kode"], technical: "Yra mėnesio išklotinės, ignoruotų eilučių peržiūra, rankiniai sutarties ir objekto susiejimai bei audito informacija.", tags: ["inside", "monthly", "contracts"] }),

    
  ];

  const fixedLayout = {
    hub: [-515, -5, 22],
    "monthly-hub": [-430, 195, 0], "contracts-hub": [-430, -155, 0],
    "monthly-excel": [-420, 115, 0], "monthly-cloud": [-300, 115, 0], "monthly-script": [-180, 115, 0], "monthly-json": [-45, 115, 0],
    dvs: [-430, -75, 0], dynamics: [-350, -75, 0], "sap-bo": [-270, -75, 0], mailbox: [-185, -75, 0], "contract-cloud": [-95, -75, 0], "contract-script": [20, -75, 0], "contract-json": [135, -75, 0],
    "sharepoint-hub": [105, 25, 0], "pad-hub": [215, 25, 0], "api-hub": [310, 25, 0], "monthly-api": [385, 100, 0], "contract-api": [385, -50, 0],
    "db-hub": [475, 25, 0], "ui-hub": [565, 25, 0],
    "subranga-sheet": [-210, 60, 35], "smd-sheet": [-145, 55, 35],
    "monthly-validation": [385, 145, 35], "contract-validation": [385, -95, 35],
    "monthly-aggregation": [455, 135, 35], "project-upsert": [455, -85, 35], "normalization": [500, -125, 42], "db-tables": [505, 90, 42], "ignored-rows": [450, 180, 42],
    "project-list": [565, 90, 35], "project-detail": [565, -45, 35]
  };
  Object.entries(fixedLayout).forEach(([id, [x, y, z]]) => {
    const item = nodes.find(candidate => candidate.id === id);
    if (item) Object.assign(item, { fx: x, fy: y, fz: z });
  });

  const link = (source, target, route, options = {}) => ({ source, target, route, important: options.important ?? true, curvature: options.curvature ?? 0.04 });
  const links = [
    link("hub", "monthly-hub", "monthly"), link("hub", "contracts-hub", "contracts"),
    link("monthly-hub", "monthly-excel", "monthly"), link("monthly-excel", "monthly-cloud", "monthly"), link("monthly-cloud", "monthly-script", "monthly"), link("monthly-script", "monthly-json", "monthly"), link("monthly-json", "sharepoint-hub", "monthly"),
    link("contracts-hub", "dvs", "contracts"), link("dvs", "dynamics", "contracts"), link("dynamics", "sap-bo", "contracts"), link("sap-bo", "mailbox", "contracts"), link("mailbox", "contract-cloud", "contracts"), link("contract-cloud", "contract-script", "contracts"), link("contract-script", "contract-json", "contracts"), link("contract-json", "sharepoint-hub", "contracts"),
    link("sharepoint-hub", "pad-hub", "bridge"), link("pad-hub", "api-hub", "bridge"), link("api-hub", "monthly-api", "monthly"), link("api-hub", "contract-api", "contracts"), link("monthly-api", "db-hub", "monthly"), link("contract-api", "db-hub", "contracts"), link("db-hub", "ui-hub", "bridge"),
    link("monthly-script", "subranga-sheet", "monthly", { important: false }), link("monthly-script", "smd-sheet", "monthly", { important: false }),
    link("monthly-api", "monthly-validation", "monthly", { important: false }), link("monthly-validation", "monthly-aggregation", "monthly", { important: false }), link("monthly-aggregation", "db-hub", "monthly", { important: false }),
    link("contract-api", "contract-validation", "contracts", { important: false }), link("contract-validation", "project-upsert", "contracts", { important: false }), link("project-upsert", "db-hub", "contracts", { important: false }),
    link("db-hub", "db-tables", "inside", { important: false }), link("db-hub", "normalization", "inside", { important: false }), link("db-hub", "ignored-rows", "inside", { important: false }), link("ui-hub", "project-list", "inside", { important: false }), link("ui-hub", "project-detail", "inside", { important: false })
  ];
  const nodesById = new Map(nodes.map(item => [item.id, item]));
  const sharedPathIds = new Set(["hub", "sharepoint-hub", "pad-hub", "api-hub", "db-hub", "ui-hub"]);
  const majorLabels = new Map([
    ["hub", "Subrangos pinigų srautas"], ["monthly-hub", "Mėnesinis aktavimas"], ["contracts-hub", "Sutartinės vertės"],
    ["monthly-excel", "Aktavimo Excel"], ["dvs", "DVS / DocLogix"], ["dynamics", "Dynamics"], ["sap-bo", "SAP BO"], ["mailbox", "DB mailbox"],
    ["monthly-cloud", "Cloud Flow"], ["monthly-script", "Office Script: subranga + SMD"], ["contract-cloud", "Cloud Flow"], ["contract-script", "Office Script: sutartinės vertės"], ["sharepoint-hub", "DB SharePoint"],
    ["monthly-json", "latest-monthly-flow.json"], ["contract-json", "latest-contracted.json"], ["pad-hub", "PAD importas"],
    ["api-hub", "Lokali API"], ["monthly-api", "POST /api/imports/monthly-flow"], ["contract-api", "POST /api/imports/contracts"],
    ["db-hub", "SQLite"], ["ui-hub", "Web UI"]
  ]);

  const statusLabels = {
    code: "Patvirtinta kode",
    process: "Pagal proceso aprašą",
    check: "Reikia patikrinti"
  };

  const graphElement = document.getElementById("graph");
  const loadingState = document.getElementById("loadingState");
  const errorState = document.getElementById("libraryError");
  if (typeof ForceGraph3D !== "function") {
    loadingState.hidden = true;
    errorState.hidden = false;
    return;
  }

  let activeFilter = "all";
  let searchTerm = "";
  let showTechnical = false;
  let selectedNode = null;
  let tourTimer = null;
  let requestCanvasLabelUpdate = () => {};
  let trackCanvasLabelTransition = () => {};
  let pointerButton = 0;
  let pointerMoved = false;
  let pointerStart = { x: 0, y: 0 };
  graphElement.addEventListener("pointerdown", event => {
    pointerButton = event.button;
    pointerMoved = false;
    pointerStart = { x: event.clientX, y: event.clientY };
  });
  graphElement.addEventListener("pointermove", event => {
    if (Math.hypot(event.clientX - pointerStart.x, event.clientY - pointerStart.y) > 5) pointerMoved = true;
  });

  const graph = ForceGraph3D({ controlType: "orbit" })(graphElement)
    .backgroundColor("rgba(0,0,0,0)")
    .graphData({ nodes, links })
    .nodeId("id")
    .nodeLabel(n => `<div style="padding:7px 9px;background:#111827ee;border:1px solid #536078;border-radius:7px;color:#edf4ff;font:600 12px Segoe UI,sans-serif">${escapeHtml(n.label)}</div>`)
    .nodeVal(n => nodeSize(n))
    .nodeColor(n => nodeColor(n))
    .nodeOpacity(0.96)
    .nodeResolution(22)
    .nodeVisibility(n => nodeVisible(n))
    .linkVisibility(l => nodeVisible(resolveNode(l.source)) && nodeVisible(resolveNode(l.target)))
    .linkColor(l => linkColor(l))
    .linkWidth(l => linkWidth(l))
    .linkOpacity(0.82)
    .linkCurvature(l => l.curvature)
    .linkDirectionalArrowLength(l => l.important ? 5.2 : 1.8)
    .linkDirectionalArrowRelPos(0.88)
    .linkDirectionalArrowColor(l => routeColor(l.route))
    .linkDirectionalParticles(l => l.important && routeMatches(l) ? (isSelectedIncoming(l) ? 7 : 3) : 0)
    .linkDirectionalParticleWidth(l => l.important ? 2.8 : 0)
    .linkDirectionalParticleSpeed(l => l.important ? 0.006 : 0)
    .linkDirectionalParticleColor(l => routeColor(l.route))
    .onNodeClick(n => focusNode(n, false))
    .onBackgroundClick(event => {
      const button = event?.button ?? pointerButton;
      if (button === 0 && !pointerMoved) closeDetails();
    })
    .enableNodeDrag(false)
    .warmupTicks(0)
    .cooldownTicks(1)
    .d3AlphaDecay(1)
    .d3VelocityDecay(0.9);

  const defaultCamera = { position: { x: 70, y: -55, z: 900 }, target: { x: 65, y: 15, z: 0 } };
  graph.cameraPosition(defaultCamera.position, defaultCamera.target, 0);
  const controls = graph.controls();
  controls.autoRotate = false;
  controls.rotateSpeed = 0.65;
  controls.zoomSpeed = 0.85;
  controls.panSpeed = 0.45;
  controls.minDistance = 350;
  controls.maxDistance = 1800;
  controls.enableDamping = true;
  controls.dampingFactor = 0.3;
  controls.dynamicDampingFactor = 0.3;
  if ("minPolarAngle" in controls) controls.minPolarAngle = Math.PI * 0.15;
  if ("maxPolarAngle" in controls) controls.maxPolarAngle = Math.PI * 0.85;
  if (typeof controls.update === "function") controls.update();
  setupCanvasLabels();
  restoreDefaultView();

  function restoreDefaultView() {
    const camera = graph.camera();
    camera.position.set(defaultCamera.position.x, defaultCamera.position.y, defaultCamera.position.z);
    camera.up.set(0, 1, 0);
    controls.target.set(defaultCamera.target.x, defaultCamera.target.y, defaultCamera.target.z);
    camera.lookAt(controls.target);
    camera.updateProjectionMatrix();
    controls.update();
    requestCanvasLabelUpdate();
  }

  function hideLoading() {
    loadingState.classList.add("is-hidden");
    window.setTimeout(() => { loadingState.hidden = true; }, 450);
  }
  graph.onEngineStop(hideLoading);
  window.setTimeout(hideLoading, 1600);

  function resolveNode(value) {
    return typeof value === "object" ? value : nodesById.get(value);
  }

  function nodeBaseColor(n) { return colors[n.group] || colors.external; }

  function routeColor(route) {
    return route === "monthly" ? colors.monthly
      : route === "contracts" ? colors.contracts
        : route === "inside" ? colors.database
          : colors.bridge;
  }

  function routeMatches(linkItem) {
    if (activeFilter === "all") return true;
    if (activeFilter === "bridge") return linkItem.route === "bridge" || ["monthly-api", "contract-api"].includes(resolveNode(linkItem.target)?.id);
    return linkItem.route === activeFilter || ((activeFilter === "monthly" || activeFilter === "contracts") && linkItem.route === "bridge");
  }

  function matchesState(n) {
    const filterMatch = activeFilter === "all"
      || n.tags.includes(activeFilter)
      || ((activeFilter === "monthly" || activeFilter === "contracts" || activeFilter === "bridge") && sharedPathIds.has(n.id))
      || (activeFilter === "bridge" && ["monthly-api", "contract-api"].includes(n.id));
    const haystack = `${n.label} ${n.description} ${n.technical} ${n.badges.join(" ")} ${groups[n.group]}`.toLocaleLowerCase("lt");
    const searchMatch = !searchTerm || haystack.includes(searchTerm);
    return filterMatch && searchMatch;
  }

  function nodeVisible(n) { return showTechnical || n.kind !== "technical"; }

  function nodeColor(n) {
    const base = nodeBaseColor(n);
    if (selectedNode?.id === n.id) return "#f3f7ff";
    return matchesState(n) ? base : dimColor(base);
  }

  function dimColor(hex) {
    const value = Number.parseInt(hex.slice(1), 16);
    const r = Math.round(((value >> 16) & 255) * 0.22 + 12);
    const g = Math.round(((value >> 8) & 255) * 0.22 + 16);
    const b = Math.round((value & 255) * 0.22 + 24);
    return `rgb(${r}, ${g}, ${b})`;
  }

  function setupCanvasLabels() {
    const layer = document.getElementById("nodeLabels");
    majorLabels.forEach((text, id) => {
      const item = nodesById.get(id);
      const label = document.createElement("button");
      label.type = "button";
      label.className = `canvas-label${item.kind === "cluster" ? " is-cluster" : ""}${id === "hub" ? " is-hub" : ""}`;
      label.textContent = text;
      label.style.setProperty("--label-color", nodeBaseColor(item));
      label.dataset.nodeId = id;
      label.title = `${text}. Spustelėkite išsamiai informacijai.`;
      label.addEventListener("click", event => {
        event.stopPropagation();
        focusNode(item);
      });
      layer.appendChild(label);
    });

    let updateScheduled = false;
    const update = () => {
      updateScheduled = false;
      layer.querySelectorAll(".canvas-label").forEach(label => {
        const item = nodesById.get(label.dataset.nodeId);
        const coords = graph.graph2ScreenCoords(item.x ?? item.fx ?? 0, item.y ?? item.fy ?? 0, item.z ?? item.fz ?? 0);
        const outside = coords.x < -80 || coords.x > graphElement.clientWidth + 80 || coords.y < -50 || coords.y > graphElement.clientHeight + 50;
        label.style.left = `${coords.x + graphElement.offsetLeft}px`;
        label.style.top = `${coords.y + graphElement.offsetTop}px`;
        label.classList.toggle("is-dimmed", !matchesState(item));
        label.classList.toggle("is-hidden", outside || !nodeVisible(item));
      });
    };
    requestCanvasLabelUpdate = () => {
      if (updateScheduled) return;
      updateScheduled = true;
      window.requestAnimationFrame(update);
    };
    trackCanvasLabelTransition = duration => {
      const endAt = performance.now() + duration;
      const track = timestamp => {
        requestCanvasLabelUpdate();
        if (timestamp < endAt) window.requestAnimationFrame(track);
      };
      window.requestAnimationFrame(track);
    };
    if (typeof controls.addEventListener === "function") controls.addEventListener("change", requestCanvasLabelUpdate);
    window.addEventListener("resize", requestCanvasLabelUpdate);
    requestCanvasLabelUpdate();
  }

  function nodeSize(n) {
    const boost = matchesState(n) && (searchTerm || activeFilter !== "all") ? 1.5 : 1;
    return n.val * boost;
  }

  function linkColor(l) {
    const source = resolveNode(l.source);
    const target = resolveNode(l.target);
    return matchesState(source) && matchesState(target) && routeMatches(l) ? routeColor(l.route) : "#3c4658";
  }

  function isSelectedIncoming(l) {
    return Boolean(selectedNode) && resolveNode(l.target)?.id === selectedNode.id;
  }

  function linkWidth(l) {
    if (!routeMatches(l)) return 0.35;
    if (isSelectedIncoming(l)) return 4.6;
    return l.important ? 3.1 : 0.8;
  }

  function refreshGraph() {
    graph.nodeColor(graph.nodeColor()).nodeVal(graph.nodeVal()).nodeVisibility(graph.nodeVisibility());
    graph.linkColor(graph.linkColor()).linkWidth(graph.linkWidth()).linkVisibility(graph.linkVisibility()).linkDirectionalParticles(graph.linkDirectionalParticles());
    document.getElementById("visibleCount").textContent = nodes.filter(nodeVisible).length;
    requestCanvasLabelUpdate();
  }

  function focusNode(n, forceCamera = false) {
    if (!n) return;
    selectedNode = n;
    controls.autoRotate = false;
    const target = { x: n.x ?? n.fx ?? 0, y: n.y ?? n.fy ?? 0, z: n.z ?? n.fz ?? 0 };
    const screen = graph.graph2ScreenCoords(target.x, target.y, target.z);
    const needsCamera = forceCamera || screen.x < 110 || screen.x > graphElement.clientWidth - 380 || screen.y < 100 || screen.y > graphElement.clientHeight - 90;
    if (needsCamera) {
      graph.cameraPosition({ x: target.x + 55, y: target.y - 65, z: target.z + 650 }, target, 850);
      trackCanvasLabelTransition(900);
    }
    showDetails(n);
    refreshGraph();
  }

  function showDetails(n) {
    document.getElementById("detailEmpty").hidden = true;
    document.getElementById("detailContent").hidden = false;
    document.getElementById("detailGroup").textContent = groups[n.group];
    document.getElementById("detailTitle").textContent = n.label;
    document.getElementById("detailDescription").textContent = n.description;
    const technical = document.getElementById("detailTechnical");
    const technicalSection = document.getElementById("technicalSection");
    technicalSection.hidden = !n.technical;
    technical.innerHTML = n.technical || "";
    document.getElementById("detailSource").textContent = n.status === "code"
      ? "Ši dalis patikrinta repozitorijos API, DTO, paslaugų, duomenų modelių arba naudotojo sąsajos kode."
      : n.status === "check"
        ? "Šiai daliai trūksta patvirtinto išorinio artefakto arba ji nesutampa su faktine kodo schema."
        : "Ši dalis paremta pateiktu verslo proceso ir automatizacijos ekrano vaizdų aprašu, ne repozitorijos kodu.";
    const badges = [...n.badges, statusLabels[n.status]];
    document.getElementById("detailBadges").innerHTML = badges.map((badge, index) => `<span class="badge ${index === badges.length - 1 ? `status-${n.status}` : ""}">${escapeHtml(badge)}</span>`).join("");
    document.getElementById("detailPanel").classList.add("is-open");
  }

  function closeDetails() {
    selectedNode = null;
    document.getElementById("detailPanel").classList.remove("is-open");
    refreshGraph();
  }

  function escapeHtml(value) {
    return String(value).replace(/[&<>'"]/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" }[char]));
  }

  document.getElementById("closePanel").addEventListener("click", closeDetails);
  document.getElementById("resetButton").addEventListener("click", () => {
    stopTour();
    closeDetails();
    controls.autoRotate = false;
    restoreDefaultView();
  });

  document.getElementById("searchInput").addEventListener("input", event => {
    searchTerm = event.target.value.trim().toLocaleLowerCase("lt");
    refreshGraph();
  });
  document.getElementById("searchInput").addEventListener("keydown", event => {
    if (event.key === "Enter" && searchTerm) {
      const match = nodes.find(n => nodeVisible(n) && matchesState(n));
      if (match) focusNode(match);
    }
  });
  document.addEventListener("keydown", event => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") {
      event.preventDefault();
      document.getElementById("searchInput").focus();
    }
    if (event.key === "Escape") closeDetails();
  });

  document.getElementById("filters").addEventListener("click", event => {
    const button = event.target.closest("[data-filter]");
    if (!button) return;
    activeFilter = button.dataset.filter;
    document.querySelectorAll(".filter").forEach(item => item.classList.toggle("is-active", item === button));
    refreshGraph();
  });

  document.getElementById("technicalToggle").addEventListener("change", event => {
    showTechnical = event.target.checked;
    refreshGraph();
  });

  const monthlyTour = ["monthly-excel", "monthly-cloud", "monthly-script", "monthly-json", "sharepoint-hub"];
  const contractTour = ["dvs", "dynamics", "sap-bo", "mailbox", "contract-cloud", "contract-script", "contract-json", "sharepoint-hub"];
  const sharedTour = ["pad-hub", "api-hub", "monthly-api", "contract-api", "db-hub", "ui-hub"];
  document.getElementById("tourButton").addEventListener("click", () => {
    if (tourTimer) { stopTour(); return; }
    activeFilter = "all";
    document.querySelectorAll(".filter").forEach(item => item.classList.toggle("is-active", item.dataset.filter === "all"));
    const route = [...monthlyTour, ...contractTour, ...sharedTour];
    let index = 0;
    const button = document.getElementById("tourButton");
    button.classList.add("is-running");
    button.textContent = "Sustabdyti turą";
    focusNode(nodesById.get(route[index++]), true);
    tourTimer = window.setInterval(() => {
      if (index >= route.length) { stopTour(); return; }
      focusNode(nodesById.get(route[index++]), true);
    }, 5200);
  });

  function stopTour() {
    if (tourTimer) window.clearInterval(tourTimer);
    tourTimer = null;
    const button = document.getElementById("tourButton");
    button.classList.remove("is-running");
    button.textContent = "Paleisti duomenų kelio turą";
  }

  window.addEventListener("resize", () => graph.width(graphElement.clientWidth).height(graphElement.clientHeight));
  document.getElementById("visibleCount").textContent = nodes.filter(nodeVisible).length;
})();
