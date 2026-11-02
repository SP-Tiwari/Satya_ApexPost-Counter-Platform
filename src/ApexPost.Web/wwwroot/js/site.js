// ApexPost interface behaviour. Everything is wired here because the CSP
// blocks inline scripts and inline event handlers. Each feature enhances
// server-rendered markup, so every page still works without JavaScript.
(function () {
    "use strict";

    const mobileQuery = window.matchMedia("(max-width: 991.98px)");
    const sidebarKey = "apexpost.sidebar";

    // Storage can be unavailable (private windows, blocked site data).
    const storage = {
        get: function (key) {
            try { return window.localStorage.getItem(key); } catch (error) { return null; }
        },
        set: function (key, value) {
            try { window.localStorage.setItem(key, value); } catch (error) { /* ignored */ }
        }
    };

    // Print buttons (data-print-page).
    document.querySelectorAll("[data-print-page]").forEach(function (button) {
        button.addEventListener("click", function () { window.print(); });
    });

    // ---------- Sidebar ----------
    function initSidebar() {
        const body = document.body;
        const toggles = document.querySelectorAll("[data-sidebar-toggle]");
        if (toggles.length === 0) {
            return;
        }

        function setExpanded(value) {
            toggles.forEach(function (toggle) {
                toggle.setAttribute("aria-expanded", String(value));
            });
        }

        function setOpen(open) {
            body.classList.toggle("sidebar-open", open);
            setExpanded(open);
        }

        if (storage.get(sidebarKey) === "collapsed") {
            body.classList.add("sidebar-collapsed");
        }
        setExpanded(!mobileQuery.matches && !body.classList.contains("sidebar-collapsed"));

        toggles.forEach(function (toggle) {
            toggle.addEventListener("click", function () {
                if (mobileQuery.matches) {
                    setOpen(!body.classList.contains("sidebar-open"));
                    return;
                }
                const collapsed = body.classList.toggle("sidebar-collapsed");
                storage.set(sidebarKey, collapsed ? "collapsed" : "expanded");
                setExpanded(!collapsed);
            });
        });

        document.querySelectorAll("[data-sidebar-close]").forEach(function (element) {
            element.addEventListener("click", function () { setOpen(false); });
        });
        document.addEventListener("keydown", function (event) {
            if (event.key === "Escape" && body.classList.contains("sidebar-open")) {
                setOpen(false);
            }
        });
        mobileQuery.addEventListener("change", function () {
            body.classList.remove("sidebar-open");
            setExpanded(!mobileQuery.matches && !body.classList.contains("sidebar-collapsed"));
        });
    }

    // ---------- Data tables: search, filter, sort and pagination ----------
    function cellText(row, index) {
        const cell = row.cells[index];
        if (!cell) {
            return "";
        }
        return (cell.getAttribute("data-sort-value") || cell.textContent).trim();
    }

    // Amounts render as "12.50 INR", so a trailing currency code is ignored.
    function asNumber(text) {
        const value = text.replace(/\s+[A-Z]{3}$/, "").replace(/,/g, "");
        return value !== "" && !Number.isNaN(Number(value)) ? Number(value) : null;
    }

    function compareText(left, right) {
        const a = asNumber(left);
        const b = asNumber(right);
        if (a !== null && b !== null) {
            return a - b;
        }
        return left.localeCompare(right, undefined, { numeric: true, sensitivity: "base" });
    }

    function createElement(tag, className, text) {
        const element = document.createElement(tag);
        if (className) {
            element.className = className;
        }
        if (text !== undefined) {
            element.textContent = text;
        }
        return element;
    }

    function initDataTable(table, tableIndex) {
        const body = table.tBodies[0];
        const head = table.tHead;
        if (!body || !head || head.rows.length === 0) {
            return;
        }

        const rows = Array.from(body.rows);
        const headers = Array.from(head.rows[0].cells);
        const pageSize = parseInt(table.dataset.pageSize || "10", 10);
        const filterIndex = table.dataset.filterColumn ? parseInt(table.dataset.filterColumn, 10) : -1;
        const container = table.closest(".table-card") || table.parentElement;
        const anchor = table.closest(".table-responsive") || table;
        const label = table.getAttribute("aria-label") || "table";
        const state = { query: "", filter: "", sortIndex: -1, direction: 1, page: 1 };

        // Toolbar
        const toolbar = createElement("div", "table-toolbar");
        const search = createElement("div", "table-search");
        const searchId = "table-search-" + tableIndex;
        const searchLabel = createElement("label", "visually-hidden", "Search " + label);
        searchLabel.htmlFor = searchId;
        const searchInput = createElement("input", "form-control form-control-sm");
        searchInput.type = "search";
        searchInput.id = searchId;
        searchInput.placeholder = table.dataset.searchPlaceholder || "Search";
        searchInput.autocomplete = "off";
        const searchIcon = document.querySelector(".table-search-icon-template");
        if (searchIcon) {
            search.appendChild(searchIcon.content.cloneNode(true));
        }
        search.append(searchLabel, searchInput);
        toolbar.appendChild(search);

        let filterSelect = null;
        if (filterIndex >= 0 && headers[filterIndex]) {
            const values = Array.from(new Set(rows.map(function (row) { return cellText(row, filterIndex); })))
                .filter(Boolean)
                .sort(compareText);
            if (values.length > 1) {
                const filterId = "table-filter-" + tableIndex;
                const filterLabel = createElement("label", "visually-hidden",
                    "Filter by " + headers[filterIndex].textContent.trim());
                filterLabel.htmlFor = filterId;
                filterSelect = createElement("select", "form-select form-select-sm table-filter");
                filterSelect.id = filterId;
                filterSelect.appendChild(new Option("All " + (table.dataset.filterLabel || headers[filterIndex].textContent.trim().toLowerCase()), ""));
                values.forEach(function (value) { filterSelect.appendChild(new Option(value, value)); });
                toolbar.append(filterLabel, filterSelect);
            }
        }

        const count = createElement("span", "table-count");
        count.setAttribute("aria-live", "polite");
        toolbar.appendChild(count);
        container.insertBefore(toolbar, anchor);

        const pager = createElement("nav", "table-pager");
        pager.setAttribute("aria-label", "Pages of " + label);
        anchor.after(pager);

        const emptyRow = createElement("tr", "table-empty-row");
        const emptyCell = createElement("td", "", "No rows match the current search or filter.");
        emptyCell.colSpan = headers.length;
        emptyRow.appendChild(emptyCell);
        emptyRow.hidden = true;
        body.appendChild(emptyRow);

        // Sortable headers
        headers.forEach(function (header, index) {
            const text = header.textContent.trim();
            if (!text || header.hasAttribute("data-no-sort")) {
                return;
            }
            const button = createElement("button", "sort-btn", text);
            button.type = "button";
            header.textContent = "";
            header.appendChild(button);
            header.setAttribute("aria-sort", "none");
            button.addEventListener("click", function () {
                state.direction = state.sortIndex === index ? -state.direction : 1;
                state.sortIndex = index;
                headers.forEach(function (other) {
                    if (other.hasAttribute("aria-sort")) {
                        other.setAttribute("aria-sort", "none");
                    }
                });
                header.setAttribute("aria-sort", state.direction === 1 ? "ascending" : "descending");
                render();
            });
        });

        function pageButton(text, page, options) {
            const button = createElement("button", "btn btn-sm btn-outline-secondary", text);
            button.type = "button";
            button.disabled = Boolean(options && options.disabled);
            if (options && options.current) {
                button.setAttribute("aria-current", "page");
            }
            if (options && options.label) {
                button.setAttribute("aria-label", options.label);
            }
            button.addEventListener("click", function () {
                state.page = page;
                render();
                anchor.scrollIntoView({ block: "nearest" });
            });
            return button;
        }

        function renderPager(pages) {
            pager.replaceChildren();
            pager.hidden = pages <= 1;
            if (pages <= 1) {
                return;
            }
            pager.appendChild(pageButton("Previous", state.page - 1, { disabled: state.page === 1 }));
            const first = Math.max(1, Math.min(state.page - 2, pages - 4));
            const last = Math.min(pages, first + 4);
            for (let page = first; page <= last; page++) {
                pager.appendChild(pageButton(String(page), page, {
                    current: page === state.page,
                    label: "Page " + page
                }));
            }
            pager.appendChild(pageButton("Next", state.page + 1, { disabled: state.page === pages }));
        }

        function render() {
            let visible = rows.filter(function (row) {
                return (!state.query || row.textContent.toLowerCase().includes(state.query)) &&
                    (!state.filter || cellText(row, filterIndex) === state.filter);
            });
            if (state.sortIndex >= 0) {
                visible = visible.slice().sort(function (a, b) {
                    return compareText(cellText(a, state.sortIndex), cellText(b, state.sortIndex)) * state.direction;
                });
            }

            const pages = Math.max(1, Math.ceil(visible.length / pageSize));
            state.page = Math.min(Math.max(1, state.page), pages);
            const start = (state.page - 1) * pageSize;
            const shown = new Set(visible.slice(start, start + pageSize));

            visible.forEach(function (row) { body.appendChild(row); });
            body.appendChild(emptyRow);
            rows.forEach(function (row) { row.hidden = !shown.has(row); });
            emptyRow.hidden = visible.length > 0;

            count.textContent = visible.length === 0
                ? "0 of " + rows.length
                : (start + 1) + "–" + (start + shown.size) + " of " + visible.length +
                    (visible.length < rows.length ? " (filtered from " + rows.length + ")" : "");
            renderPager(pages);
        }

        searchInput.addEventListener("input", function () {
            state.query = searchInput.value.trim().toLowerCase();
            state.page = 1;
            render();
        });
        if (filterSelect) {
            filterSelect.addEventListener("change", function () {
                state.filter = filterSelect.value;
                state.page = 1;
                render();
            });
        }

        render();
    }

    // ---------- Forms: validation feedback, confirmation and busy state ----------
    function setBusy(form, submitter) {
        const button = submitter || form.querySelector("[type=submit]");
        form.setAttribute("aria-busy", "true");
        if (!button) {
            return;
        }
        // Disabled after the submission has started, so the form still posts.
        window.setTimeout(function () {
            button.disabled = true;
            button.classList.add("is-busy");
            const spinner = createElement("span", "spinner-border spinner-border-sm");
            spinner.setAttribute("aria-hidden", "true");
            button.prepend(spinner);
        }, 0);
    }

    function clearBusy() {
        document.querySelectorAll("form[aria-busy=true]").forEach(function (form) {
            form.removeAttribute("aria-busy");
            form.querySelectorAll(".is-busy").forEach(function (button) {
                button.disabled = false;
                button.classList.remove("is-busy");
                button.querySelectorAll(".spinner-border").forEach(function (spinner) { spinner.remove(); });
            });
        });
    }

    function initForms() {
        const dialog = document.getElementById("confirm-dialog");
        const modal = dialog && window.bootstrap ? window.bootstrap.Modal.getOrCreateInstance(dialog) : null;
        let pending = null;

        if (modal) {
            dialog.querySelector("[data-dialog-accept]").addEventListener("click", function () {
                const request = pending;
                pending = null;
                modal.hide();
                if (request) {
                    request.form.dataset.confirmed = "true";
                    request.form.requestSubmit(request.submitter || undefined);
                }
            });
        }

        // Runs after jQuery validation, which stops invalid submissions first.
        document.addEventListener("submit", function (event) {
            const form = event.target;
            if (!(form instanceof HTMLFormElement) || event.defaultPrevented) {
                return;
            }

            if (form.classList.contains("needs-validation") && !form.checkValidity()) {
                event.preventDefault();
                form.classList.add("was-validated");
                const invalid = form.querySelector(":invalid");
                if (invalid) {
                    invalid.focus();
                }
                return;
            }

            if (form.dataset.confirm && form.dataset.confirmed !== "true") {
                if (modal) {
                    event.preventDefault();
                    pending = { form: form, submitter: event.submitter };
                    dialog.querySelector("[data-dialog-title]").textContent = form.dataset.confirmTitle || "Please confirm";
                    dialog.querySelector("[data-dialog-message]").textContent = form.dataset.confirm;
                    const accept = dialog.querySelector("[data-dialog-accept]");
                    accept.textContent = form.dataset.confirmAction || "Confirm";
                    accept.className = "btn " + (form.dataset.confirmTone === "warning" ? "btn-warning" : "btn-danger");
                    modal.show();
                    return;
                }
                if (!window.confirm(form.dataset.confirm)) {
                    event.preventDefault();
                    return;
                }
            }

            delete form.dataset.confirmed;
            if (!form.hasAttribute("data-no-busy")) {
                setBusy(form, event.submitter);
            }
        });

        // A page restored from the back/forward cache keeps its disabled buttons.
        window.addEventListener("pageshow", function (event) {
            if (event.persisted) {
                clearBusy();
            }
        });
    }

    // ---------- Toasts ----------
    function initToasts() {
        if (!window.bootstrap) {
            return;
        }
        document.querySelectorAll(".toast[data-toast]").forEach(function (element) {
            const persistent = element.dataset.toast === "persistent";
            const toast = window.bootstrap.Toast.getOrCreateInstance(element, {
                autohide: !persistent,
                delay: 6000
            });
            toast.show();
        });
    }

    // ---------- Tabs: reopen the tab used last ----------
    function initTabs() {
        if (!window.bootstrap) {
            return;
        }
        document.querySelectorAll("[data-tabs-key]").forEach(function (list) {
            const key = list.dataset.tabsKey;
            const saved = storage.get(key);
            const tab = saved ? list.querySelector('[data-bs-target="' + saved + '"]') : null;
            if (tab) {
                window.bootstrap.Tab.getOrCreateInstance(tab).show();
            }
            list.addEventListener("shown.bs.tab", function (event) {
                storage.set(key, event.target.getAttribute("data-bs-target"));
            });
        });
    }

    initSidebar();
    initTabs();
    document.querySelectorAll("table[data-table]").forEach(initDataTable);
    initForms();
    initToasts();
})();
