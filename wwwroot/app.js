const moneyFormatter = new Intl.NumberFormat("en-US", {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2
});

const loading = document.querySelector("#loading");
const error = document.querySelector("#error");
const empty = document.querySelector("#empty");
const noProjectMatches = document.querySelector("#noProjectMatches");
const projectRegister = document.querySelector("#projectRegister");
const projectRegisterBody = document.querySelector("#projectRegisterBody");
const projectCount = document.querySelector("#projectCount");
const projectSearch = document.querySelector("#projectSearch");
const projectSearchClear = document.querySelector("#projectSearchClear");
const responsibleFilter = document.querySelector("#responsibleFilter");
const engineerFilter = document.querySelector("#engineerFilter");
const statusFilter = document.querySelector("#statusFilter");
const warningsFilter = document.querySelector("#warningsFilter");
const projectListSubtitle = document.querySelector("#projectListSubtitle");
const viewTabs = document.querySelectorAll(".view-tab");
const statusStrip = document.querySelector("#statusStrip");
const projectPageKpis = document.querySelector("#projectPageKpis");

/* whole euros for at-a-glance portfolio figures; tables keep 2 decimals */
const compactMoneyFormatter = new Intl.NumberFormat("en-US", {
  minimumFractionDigits: 0,
  maximumFractionDigits: 0
});

let projectSummaries = [];
let activeProjectSummaries = [];
let inactiveProjectSummaries = [];
let currentProjectView = "active";
let projectSort = { key: "project", direction: "asc" };

function show(el) { el.hidden = false; }
function hide(el) { el.hidden = true; }
function money(value) { return moneyFormatter.format(Number(value ?? 0)); }
function numberValue(value) { return Number(value ?? 0); }

function parseProjectObjectCode(value) {
  const cleaned = String(value ?? "").trim();
  const dashIndex = cleaned.lastIndexOf("-");
  if (dashIndex <= 0 || dashIndex === cleaned.length - 1) {
    return { parentProjectCode: cleaned, objectNumber: cleaned, objectCode: "" };
  }
  return {
    parentProjectCode: cleaned.slice(0, dashIndex),
    objectNumber: cleaned,
    objectCode: cleaned.slice(dashIndex + 1)
  };
}

function summarizeList(items, max = 1) {
  const unique = [...new Set((items ?? []).filter(Boolean).map(s => s.trim()).filter(Boolean))];
  if (unique.length === 0) return null;
  if (unique.length <= max) return unique.join(", ");
  const rest = unique.length - max;
  return `${unique.slice(0, max).join(", ")} +${rest} daugiau`;
}

function projectDisplayFields(project) {
  const objects = project.objects ?? [];
  const responsible = project.responsible
    || summarizeList(objects.flatMap(o => o.responsibles ?? []));
  const engineer = project.engineer
    || summarizeList(objects.flatMap(o => o.engineers ?? []));

  return {
    projectName: project.projectName || null,
    objectCount: Number(project.objectCount ?? objects.length ?? 0),
    responsible: responsible || null,
    engineer: engineer || null
  };
}

function projectHref(project) {
  const objects = project.objects ?? [];
  const parentCode = parseProjectObjectCode(project.projectCode).parentProjectCode;
  if (objects.length === 1) {
    return `/project.html?projectCode=${encodeURIComponent(project.projectCode)}&objectNumber=${encodeURIComponent(objects[0].objectNumber)}`;
  }
  return `/project.html?projectCode=${encodeURIComponent(parentCode || project.projectCode)}`;
}

function textCell(text, className) {
  const td = document.createElement("td");
  if (className) td.className = className;
  td.textContent = text ?? "";
  return td;
}

function moneyCell(value, label) {
  const td = textCell(money(value), "col-money");
  if (Number(value) < 0) td.classList.add("is-negative");
  if (Number(value ?? 0) === 0) td.classList.add("cell-quiet");
  if (label) td.dataset.label = label;
  return td;
}

function statusClass(status) {
  if (status === "Trūksta sutarties") return "pill-warn";
  if (status === "Viršyta riba") return "pill-danger";
  if (status === "Pasiekta riba" || status === "Artėja prie ribos") return "pill-warn";
  return "pill-ok";
}

function statusCell(project) {
  const td = document.createElement("td");
  const status = project.status || "Pagal planą";
  if (status === "Pagal planą") {
    const quiet = document.createElement("span");
    quiet.className = "status-quiet";
    quiet.textContent = "Pagal planą";
    td.append(quiet);
    return td;
  }
  const pill = document.createElement("span");
  pill.className = `pill ${statusClass(status)}`;
  pill.textContent = status;
  td.append(pill);
  return td;
}

function projectNameCell(fields) {
  const td = document.createElement("td");
  td.className = "col-name";

  if (!fields.projectName) {
    td.textContent = "-";
    td.style.color = "var(--muted-2)";
    return td;
  }

  const name = document.createElement("span");
  name.className = "project-name-main";
  name.textContent = fields.projectName;
  /* names truncate (2-line clamp / mobile ellipsis); expose full text on hover */
  name.title = fields.projectName;
  td.append(name);

  if (fields.objectCount > 1) {
    const detail = document.createElement("span");
    detail.className = "project-name-sub";
    detail.textContent = `${fields.objectCount} objekt${fields.objectCount === 1 ? "as" : "ai"}`;
    td.append(detail);
  }

  return td;
}

function projectRow(project) {
  const parentCode = parseProjectObjectCode(project.projectCode).parentProjectCode;
  const href = projectHref(project);
  const fields = projectDisplayFields(project);

  const tr = document.createElement("tr");
  tr.tabIndex = 0;
  tr.dataset.href = href;
  if (project.status === "Viršyta riba") tr.classList.add("is-over-limit");

  const codeTd = document.createElement("td");
  codeTd.className = "col-code";
  const a = document.createElement("a");
  a.href = href;
  a.textContent = parentCode;
  codeTd.append(a);
  tr.append(codeTd);

  tr.append(projectNameCell(fields));

  const resTd = textCell(fields.responsible ?? "-");
  resTd.dataset.label = "Atsakingas";
  if (!fields.responsible) resTd.style.color = "var(--muted-2)";
  tr.append(resTd);

  const engTd = textCell(fields.engineer ?? "-");
  engTd.dataset.label = "Inžinierius";
  if (!fields.engineer) engTd.style.color = "var(--muted-2)";
  tr.append(engTd);

  tr.append(moneyCell(project.amountWithoutVat, "Sąskaitose"));
  tr.append(moneyCell(project.remaining, "Likutis"));

  const statusTd = statusCell(project);
  statusTd.dataset.label = "Būsena";
  tr.append(statusTd);

  const warnings = numberValue(project.warningsCount) || (project.status !== "Pagal planą" ? 1 : 0);
  const warningTd = document.createElement("td");
  warningTd.className = "col-money warning-count";
  warningTd.dataset.label = "Įspėjimai";
  if (warnings > 0) {
    const badge = document.createElement("span");
    badge.className = project.status === "Viršyta riba" ? "count-badge" : "count-badge is-warn";
    badge.textContent = String(warnings);
    warningTd.append(badge);
  } else {
    warningTd.textContent = "—";
    warningTd.classList.add("cell-quiet");
  }
  tr.append(warningTd);

  const chevTd = document.createElement("td");
  chevTd.className = "col-chev";
  chevTd.textContent = "›";
  tr.append(chevTd);

  return tr;
}

function projectMatchesQuery(project, query) {
  if (!query) return true;
  const q = query.toLowerCase();
  const parentCode = parseProjectObjectCode(project.projectCode).parentProjectCode.toLowerCase();
  if (parentCode.includes(q)) return true;
  if ((project.projectName ?? "").toLowerCase().includes(q)) return true;
  if ((project.responsible ?? "").toLowerCase().includes(q)) return true;
  if ((project.engineer ?? "").toLowerCase().includes(q)) return true;

  for (const obj of project.objects ?? []) {
    if ((obj.objectName ?? "").toLowerCase().includes(q)) return true;
    if ((obj.responsibles ?? []).some(r => r.toLowerCase().includes(q))) return true;
    if ((obj.engineers ?? []).some(e => e.toLowerCase().includes(q))) return true;
  }

  return false;
}

function projectHasWarning(project) {
  return numberValue(project.warningsCount) > 0 || project.status !== "Pagal planą";
}

function projectMatchesFilters(project) {
  const fields = projectDisplayFields(project);
  if (responsibleFilter.value && fields.responsible !== responsibleFilter.value) return false;
  if (engineerFilter.value && fields.engineer !== engineerFilter.value) return false;
  if (statusFilter.value && project.status !== statusFilter.value) return false;
  if (warningsFilter.checked && !projectHasWarning(project)) return false;
  return true;
}

function sortText(value) {
  return String(value ?? "").toLocaleLowerCase();
}

function projectSortValue(project, key) {
  const fields = projectDisplayFields(project);
  if (key === "project") return sortText(parseProjectObjectCode(project.projectCode).parentProjectCode);
  if (key === "name") return sortText(fields.projectName);
  if (key === "responsible") return sortText(fields.responsible);
  if (key === "engineer") return sortText(fields.engineer);
  if (key === "invoiced") return numberValue(project.amountWithoutVat);
  if (key === "remaining") return numberValue(project.remaining);
  if (key === "warnings") return numberValue(project.warningsCount);
  if (key === "status") return sortText(project.status);
  return "";
}

function sortProjects(projects) {
  const { key, direction } = projectSort;
  const multiplier = direction === "asc" ? 1 : -1;
  return [...projects].sort((a, b) => {
    const av = projectSortValue(a, key);
    const bv = projectSortValue(b, key);
    if (typeof av === "number" && typeof bv === "number") return (av - bv) * multiplier;
    return String(av).localeCompare(String(bv), undefined, { numeric: true, sensitivity: "base" }) * multiplier;
  });
}

function setSortIndicators() {
  document.querySelectorAll(".sort-button").forEach((button) => {
    const key = button.dataset.sort;
    const isCurrent = key === projectSort.key;
    button.dataset.direction = isCurrent ? projectSort.direction : "";
    /* aria-sort belongs on the th, with the full ascending/descending tokens */
    button.closest("th")?.setAttribute(
      "aria-sort",
      isCurrent ? (projectSort.direction === "asc" ? "ascending" : "descending") : "none"
    );
  });
}

function collectFilterOptions(getValue) {
  return [...new Set(projectSummaries
    .map((project) => getValue(projectDisplayFields(project)))
    .filter(Boolean))]
    .sort((a, b) => a.localeCompare(b, undefined, { sensitivity: "base" }));
}

function fillFilter(select, label, values) {
  const current = select.value;
  select.replaceChildren(
    Object.assign(document.createElement("option"), { value: "", textContent: label }),
    ...values.map((value) => Object.assign(document.createElement("option"), { value, textContent: value }))
  );
  if (values.includes(current)) select.value = current;
}

function populateFilters() {
  fillFilter(responsibleFilter, "Visi atsakingi", collectFilterOptions((fields) => fields.responsible));
  fillFilter(engineerFilter, "Visi inžinieriai", collectFilterOptions((fields) => fields.engineer));
}

function setCurrentProjects() {
  projectSummaries = currentProjectView === "inactive"
    ? inactiveProjectSummaries
    : activeProjectSummaries;
}

function setProjectView(view) {
  currentProjectView = view;
  setCurrentProjects();
  projectListSubtitle.textContent = view === "inactive"
    ? "Projektai, nerasti naujausiame sutarčių importe"
    : "Aktyvūs projektai iš naujausio sutarčių importo";
  viewTabs.forEach((tab) => {
    const isActive = tab.dataset.view === view;
    tab.classList.toggle("is-active", isActive);
    tab.setAttribute("aria-pressed", String(isActive));
  });
  populateFilters();
  renderProjects();
}

let firstProjectRender = true;

/* Status overview strip: glanceable risk counts that double as filter shortcuts. */
const STRIP_STATUSES = [
  { status: "Viršyta riba", tone: "danger" },
  { status: "Trūksta sutarties", tone: "warn" }
];

function statusStripChip({ label, count, tone, isActive, onClick, title }) {
  const chip = document.createElement("button");
  chip.type = "button";
  chip.className = `strip-chip${tone ? ` strip-${tone}` : ""}${isActive ? " is-active" : ""}`;
  chip.setAttribute("aria-pressed", String(isActive));
  if (title) chip.title = title;

  chip.setAttribute(
    "aria-label",
    `${label}: ${count} projekt${count === 1 ? "as" : "ai"}. ${isActive ? "Filtras aktyvus, spustelėkite, kad išvalytumėte." : "Spustelėkite, kad filtruotumėte."}`
  );

  const value = document.createElement("span");
  value.className = "strip-chip-count";
  value.textContent = String(count);
  const text = document.createElement("span");
  text.className = "strip-chip-label";
  text.textContent = label;
  chip.append(value, text);
  chip.addEventListener("click", onClick);
  return chip;
}

function renderStatusStrip() {
  if (!statusStrip) return;
  if (projectSummaries.length === 0) { hide(statusStrip); return; }

  const counts = new Map(STRIP_STATUSES.map(({ status }) => [status, 0]));
  let healthy = 0;
  for (const project of projectSummaries) {
    if (counts.has(project.status)) counts.set(project.status, counts.get(project.status) + 1);
    if (!projectHasWarning(project)) healthy += 1;
  }

  const chips = [
    statusStripChip({
      label: "Pagal planą",
      count: healthy,
      tone: "ok",
      isActive: statusFilter.value === "Pagal planą",
      title: "Projektai be įspėjimų",
      onClick: () => {
        statusFilter.value = statusFilter.value === "Pagal planą" ? "" : "Pagal planą";
        warningsFilter.checked = false;
        renderProjects();
      }
    }),
    ...STRIP_STATUSES
      .filter(({ status }) => counts.get(status) > 0)
      .map(({ status, tone }) => statusStripChip({
        label: status,
        count: counts.get(status),
        tone,
        isActive: statusFilter.value === status,
        title: `Rodyti tik „${status}“ projektus`,
        onClick: () => {
          statusFilter.value = statusFilter.value === status ? "" : status;
          warningsFilter.checked = false;
          renderProjects();
        }
      }))
  ];

  statusStrip.replaceChildren(...chips);
  show(statusStrip);
}

/* Portfolio summary under the page header. Counts and totals come from the
   already-loaded project list for the current view; filters do not change it. */
function pageKpi(value, label, tone) {
  const cell = document.createElement("div");
  cell.className = "page-kpi";
  const valueEl = document.createElement("span");
  valueEl.className = `page-kpi-value${tone ? ` is-${tone}` : ""}`;
  valueEl.textContent = value;
  const labelEl = document.createElement("span");
  labelEl.className = "page-kpi-label";
  labelEl.textContent = label;
  cell.append(valueEl, labelEl);
  return cell;
}

function renderPageKpis() {
  if (!projectPageKpis) return;
  if (projectSummaries.length === 0 || currentProjectView === "inactive") {
    hide(projectPageKpis);
    return;
  }

  let overLimit = 0;
  let missingContract = 0;
  let projectValue = 0;
  let subcontractorInvoiced = 0;
  for (const project of projectSummaries) {
    if (project.status === "Viršyta riba") overLimit += 1;
    if (project.status === "Trūksta sutarties") missingContract += 1;
    projectValue += numberValue(project.projectValue);
    subcontractorInvoiced += numberValue(project.amountWithoutVat);
  }

  projectPageKpis.replaceChildren(
    pageKpi(String(projectSummaries.length), "Projektai"),
    pageKpi(String(overLimit), "Viršyta riba", overLimit > 0 ? "danger" : "quiet"),
    pageKpi(String(missingContract), "Trūksta sutarties", missingContract > 0 ? "warn" : "quiet"),
    pageKpi(`${compactMoneyFormatter.format(projectValue)} €`, "Projektų vertė"),
    pageKpi(`${compactMoneyFormatter.format(subcontractorInvoiced)} €`, "Subrangovų suma")
  );
  show(projectPageKpis);
}

function updateRegisterFade() {
  const scroller = projectRegister?.querySelector(".register-scroll");
  if (!scroller) return;
  const more = scroller.scrollWidth - scroller.clientWidth - scroller.scrollLeft > 1;
  projectRegister.classList.toggle("can-scroll-right", more);
}

function renderProjects() {
  const query = projectSearch.value.trim();
  const filtered = sortProjects(projectSummaries.filter(p => projectMatchesQuery(p, query) && projectMatchesFilters(p)));

  hide(noProjectMatches);
  hide(projectRegister);
  setSortIndicators();
  renderPageKpis();
  renderStatusStrip();
  projectSearchClear.hidden = query.length === 0;

  const total = projectSummaries.length;
  projectCount.textContent = total === 0
    ? "Projektų nėra"
    : `${filtered.length} iš ${total} projekt${total === 1 ? "as" : "ų"}`;

  if (total === 0) { show(empty); return; }
  hide(empty);
  if (filtered.length === 0) { show(noProjectMatches); return; }

  const rows = filtered.map(projectRow);
  if (firstProjectRender) {
    rows.slice(0, 12).forEach((tr, i) => {
      tr.classList.add("row-enter");
      tr.style.animationDelay = `${i * 15}ms`;
    });
    firstProjectRender = false;
  }
  projectRegisterBody.replaceChildren(...rows);
  show(projectRegister);
  updateRegisterFade();
}

projectRegister?.querySelector(".register-scroll")?.addEventListener("scroll", updateRegisterFade, { passive: true });
window.addEventListener("resize", updateRegisterFade);

projectRegisterBody?.addEventListener("click", (event) => {
  const tr = event.target.closest("tr");
  if (!tr || tr.parentElement !== projectRegisterBody) return;
  if (event.target.closest("a")) return;
  if (tr.dataset.href) window.location.href = tr.dataset.href;
});

projectRegisterBody?.addEventListener("keydown", (event) => {
  if (event.key !== "Enter" && event.key !== " ") return;
  const tr = event.target.closest("tr");
  if (!tr || tr.parentElement !== projectRegisterBody) return;
  event.preventDefault();
  if (tr.dataset.href) window.location.href = tr.dataset.href;
});

async function loadProjects() {
  try {
    hide(empty); hide(noProjectMatches); hide(error); hide(projectRegister);
    show(loading);

    const response = await fetch("/api/projects");
    if (!response.ok) throw new Error(`Projects request failed with status ${response.status}.`);

    const data = await response.json();
    const projects = data.projects ?? [];
    const codes = data.projectCodes ?? [];

    activeProjectSummaries = (data.activeProjects ?? data.projects ?? projects);
    inactiveProjectSummaries = data.inactiveProjects ?? [];
    if (activeProjectSummaries.length === 0 && projects.length === 0 && codes.length > 0) {
      activeProjectSummaries = codes.map(projectCode => ({ projectCode, objects: [] }));
    }
    setCurrentProjects();

    hide(loading);
    populateFilters();
    renderProjects();
  } catch (exception) {
    hide(loading);
    projectCount.textContent = "Nepavyko įkelti projektų";
    error.replaceChildren();
    const message = document.createElement("span");
    message.textContent = `Nepavyko įkelti projektų. ${exception.message}`;
    const retry = document.createElement("button");
    retry.type = "button";
    retry.className = "btn";
    retry.textContent = "Bandyti dar kartą";
    retry.addEventListener("click", () => loadProjects());
    error.append(message, retry);
    show(error);
  }
}

projectSearch.addEventListener("input", renderProjects);
projectSearchClear.addEventListener("click", () => {
  projectSearch.value = "";
  projectSearch.focus();
  renderProjects();
});
[responsibleFilter, engineerFilter, statusFilter, warningsFilter].forEach((control) => {
  control.addEventListener("change", renderProjects);
});
document.querySelectorAll(".sort-button").forEach((button) => {
  button.addEventListener("click", () => {
    const key = button.dataset.sort;
    if (projectSort.key === key) {
      projectSort.direction = projectSort.direction === "asc" ? "desc" : "asc";
    } else {
      projectSort = { key, direction: "asc" };
    }
    renderProjects();
  });
});
viewTabs.forEach((tab) => {
  tab.addEventListener("click", () => setProjectView(tab.dataset.view || "active"));
});

async function loadImportStatus() {
  try {
    const response = await fetch("/api/imports/monthly-flow/status", { cache: "no-store" });
    if (!response.ok) return;
    const payload = await response.json();
    const importedAt = payload?.latestImport?.importedAt;
    if (!importedAt) return;
    const formatter = new Intl.DateTimeFormat("lt-LT", {
      month: "short", day: "numeric", year: "numeric", hour: "2-digit", minute: "2-digit", hour12: false
    });
    const stamp = formatter.format(new Date(importedAt));
    const footerText = document.querySelector("#footerImportText");
    if (footerText) footerText.textContent = `Paskutinis importas: ${stamp}`;
    const headerText = document.querySelector("#latestImportText");
    const headerMeta = document.querySelector("#latestImportMeta");
    if (headerText && headerMeta) {
      headerText.textContent = `Naujausias importas: ${stamp}`;
      headerMeta.hidden = false;
    }
  } catch {
    /* import meta is optional */
  }
}

/* Nav hash shortcuts: #exceptions narrows to warning projects, #filters focuses search. */
function applyNavHash() {
  if (window.location.hash === "#exceptions") {
    warningsFilter.checked = true;
    statusFilter.value = "";
    renderProjects();
    projectRegister?.scrollIntoView({ behavior: "smooth", block: "start" });
  } else if (window.location.hash === "#filters") {
    document.querySelector("#filters")?.scrollIntoView({ behavior: "smooth", block: "start" });
    projectSearch.focus({ preventScroll: true });
  }
}
window.addEventListener("hashchange", applyNavHash);

loadProjects().then(applyNavHash);
loadImportStatus();
