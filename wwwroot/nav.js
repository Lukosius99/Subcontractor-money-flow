/* Shared left-side project navigation.
   A "stripe" (hamburger) button in the app-bar opens a slide-in sidebar with a
   compact, searchable list of every project. Used by both the project register
   (index) and the project detail page so you can jump between projects without
   going back to the list first. */
(function () {
  const toggle = document.querySelector("#navToggle");
  const sidebar = document.querySelector("#projectNav");
  const backdrop = document.querySelector("#navBackdrop");
  if (!toggle || !sidebar || !backdrop) return;

  const listEl = sidebar.querySelector("#projectNavList");
  const searchEl = sidebar.querySelector("#projectNavSearch");
  const closeBtn = sidebar.querySelector("#projectNavClose");
  const countEl = sidebar.querySelector("#projectNavCount");

  let projects = null;
  let loaded = false;
  let loading = false;

  /* On the detail page the URL carries the project we're viewing — used to
     mark the matching sidebar entry as the current location. */
  const params = new URLSearchParams(window.location.search);
  const currentParent = parentCode(params.get("projectCode") || "");

  function parentCode(value) {
    const cleaned = String(value ?? "").trim();
    const dash = cleaned.lastIndexOf("-");
    if (dash <= 0 || dash === cleaned.length - 1) return cleaned;
    return cleaned.slice(0, dash);
  }

  function hrefFor(project) {
    const objects = project.objects ?? [];
    const parent = parentCode(project.projectCode);
    if (objects.length === 1) {
      return `/project.html?projectCode=${encodeURIComponent(project.projectCode)}&objectNumber=${encodeURIComponent(objects[0].objectNumber)}`;
    }
    return `/project.html?projectCode=${encodeURIComponent(parent || project.projectCode)}`;
  }

  function renderList(filter) {
    const q = (filter || "").trim().toLowerCase();
    const items = (projects || []).filter((p) => {
      if (!q) return true;
      const code = parentCode(p.projectCode).toLowerCase();
      const name = (p.projectName || "").toLowerCase();
      return code.includes(q) || name.includes(q);
    });

    listEl.replaceChildren();

    if (countEl) {
      const total = (projects || []).length;
      countEl.textContent = q ? `${items.length} iš ${total}` : `${total}`;
    }

    if (items.length === 0) {
      const empty = document.createElement("p");
      empty.className = "proj-nav-empty";
      empty.textContent = projects && projects.length ? "Projektų nerasta." : "Projektų nėra.";
      listEl.append(empty);
      return;
    }

    const frag = document.createDocumentFragment();
    for (const project of items) {
      const parent = parentCode(project.projectCode);
      const a = document.createElement("a");
      a.className = "proj-nav-item";
      a.href = hrefFor(project);
      if (currentParent && parent === currentParent) {
        a.classList.add("is-current");
        a.setAttribute("aria-current", "page");
      }

      const code = document.createElement("span");
      code.className = "proj-nav-code";
      code.textContent = parent;
      a.append(code);

      if (project.projectName) {
        const name = document.createElement("span");
        name.className = "proj-nav-name";
        name.textContent = project.projectName;
        name.title = project.projectName;
        a.append(name);
      }

      frag.append(a);
    }
    listEl.append(frag);
  }

  async function loadProjects() {
    if (loaded || loading) return;
    loading = true;
    listEl.replaceChildren(
      Object.assign(document.createElement("p"), {
        className: "proj-nav-empty",
        textContent: "Įkeliama…"
      })
    );
    try {
      const response = await fetch("/api/projects");
      if (!response.ok) throw new Error(String(response.status));
      const data = await response.json();
      /* sidebar lists only active projects — those in the latest contract import */
      projects = data.activeProjects ?? data.projects ?? [];
      loaded = true;
      renderList(searchEl ? searchEl.value : "");
    } catch {
      listEl.replaceChildren(
        Object.assign(document.createElement("p"), {
          className: "proj-nav-empty",
          textContent: "Nepavyko įkelti projektų."
        })
      );
    } finally {
      loading = false;
    }
  }

  let lastFocused = null;

  function openNav() {
    lastFocused = document.activeElement;
    /* lock the page behind the sidebar; pad for the removed scrollbar so the
       app-bar and content don't jump sideways when scrolling is disabled */
    const scrollbar = window.innerWidth - document.documentElement.clientWidth;
    if (scrollbar > 0) document.body.style.paddingRight = `${scrollbar}px`;
    /* the page scroller is the root <html> element, so lock it there — locking
       only <body> leaves wheel/touch scroll chaining to the page underneath */
    document.documentElement.style.overflow = "hidden";
    document.body.classList.add("nav-open");
    toggle.setAttribute("aria-expanded", "true");
    sidebar.setAttribute("aria-hidden", "false");
    loadProjects();
    /* focus the filter so you can type-to-find straight away */
    window.requestAnimationFrame(() => searchEl && searchEl.focus());
  }

  function closeNav() {
    if (!document.body.classList.contains("nav-open")) return;
    document.body.classList.remove("nav-open");
    document.body.style.paddingRight = "";
    document.documentElement.style.overflow = "";
    toggle.setAttribute("aria-expanded", "false");
    sidebar.setAttribute("aria-hidden", "true");
    if (lastFocused && typeof lastFocused.focus === "function") lastFocused.focus();
  }

  toggle.addEventListener("click", () => {
    if (document.body.classList.contains("nav-open")) closeNav();
    else openNav();
  });
  backdrop.addEventListener("click", closeNav);
  closeBtn && closeBtn.addEventListener("click", closeNav);
  searchEl && searchEl.addEventListener("input", () => renderList(searchEl.value));

  document.addEventListener("keydown", (event) => {
    if (event.key === "Escape" && document.body.classList.contains("nav-open")) {
      event.stopPropagation();
      closeNav();
    }
  });
})();
