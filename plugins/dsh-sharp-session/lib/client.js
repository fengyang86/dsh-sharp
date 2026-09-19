window.__ModuleLoader__.load({
	id: "@yangfeng/dsh-sharp-session",
	factory: (require) => {
		var module = { exports: {} };
		var exports = module.exports;
		Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
		let react = require("react");
		let _deepseek_ai_dsh_client_ui_primitives = require("@deepseek-ai/dsh-client-ui-primitives");
		let react_jsx_runtime = require("react/jsx-runtime");
		let _deepseek_ai_dsh_client_store = require("@deepseek-ai/dsh-client-store");
		//#region src/client/shortcut.ts
		function readFeatureFlags(locationLike = window.location) {
			const query = new URLSearchParams(locationLike.search);
			const hash = new URLSearchParams(locationLike.hash.replace(/^#/, ""));
			const enabled = (name) => (hash.get(`dshsharp-${name}`) ?? query.get(`dshsharp-${name}`)) !== "0";
			return {
				escStop: enabled("esc-stop"),
				copyId: enabled("copy-id"),
				openWorkspace: enabled("open-workspace"),
				trayNavigation: enabled("tray-navigation")
			};
		}
		/** 托盘会话跳转：等待 DSH 会话列表就绪的重试节奏（整页重载后列表数据晚于插件加载）。 */
		const NAVIGATION_RETRY_INTERVAL_MS = 150;
		const NAVIGATION_RETRY_LIMIT = 67;
		/**
		* 处理桌面托盘传入的会话地址，并交给 DSH 官方 sessions.open。
		* sessions.open 要求目标会话已存在于客户端列表（否则抛错），而整页重载后
		* 插件加载早于列表 baseline；因此在重试窗口内等待目标出现后再打开。
		*/
		function installSessionNavigation(sessions) {
			let generation = 0;
			let timer;
			const waitForSessionAndOpen = (id) => {
				const current = ++generation;
				let attempts = 0;
				const attempt = () => {
					if (current !== generation) return;
					if (sessions.list.getSnapshot().byId[id] !== void 0) {
						try {
							sessions.open(id);
							history.replaceState(null, "", window.location.pathname);
						} catch (error) {
							console.error("[dsh-sharp-session] 打开托盘会话失败:", error);
						}
						return;
					}
					if (++attempts > NAVIGATION_RETRY_LIMIT) {
						console.error("[dsh-sharp-session] 打开托盘会话失败: 会话列表就绪超时", id);
						return;
					}
					timer = setTimeout(attempt, NAVIGATION_RETRY_INTERVAL_MS);
				};
				attempt();
			};
			const openFromHash = () => {
				const match = window.location.hash.match(/(?:^#|&)dsh-session=([^&]+)/) ?? window.location.search.match(/[?&]dsh-session=([^&]+)/);
				if (match === null) return;
				waitForSessionAndOpen(decodeURIComponent(match[1]));
			};
			window.addEventListener("hashchange", openFromHash);
			openFromHash();
			return () => {
				generation++;
				if (timer !== void 0) clearTimeout(timer);
				window.removeEventListener("hashchange", openFromHash);
			};
		}
		const TRANSIENT_LAYER_SELECTOR = [
			"[aria-modal=\"true\"]",
			"[role=\"dialog\"]",
			"[role=\"menu\"]",
			"[role=\"listbox\"]"
		].join(",");
		/**
		* 安装 Esc 停止当前会话的网页快捷键。
		* 临时界面拥有 Esc 的优先权；插件只处理未被其他界面消费的按键。
		*/
		function installSessionShortcuts(sessions, documentRoot = document) {
			let cancelPending = false;
			const onKeyDown = (event) => {
				if (event.key !== "Escape" || event.repeat || event.isComposing || cancelPending) return;
				const transientLayerWasOpen = documentRoot.querySelector(TRANSIENT_LAYER_SELECTOR) !== null;
				queueMicrotask(() => {
					if (event.defaultPrevented || transientLayerWasOpen || cancelPending) return;
					const snapshot = sessions.list.getSnapshot();
					const sessionId = snapshot.current;
					if (sessionId === void 0 || snapshot.byId[sessionId]?.running !== true) return;
					const session = sessions.binding(sessionId)?.session;
					if (session === void 0) return;
					cancelPending = true;
					session.cancel().then((result) => {
						if (!result.ok) console.error("[dsh-sharp-session] 停止会话失败:", result.error?.message ?? "未知错误");
					}).catch((error) => {
						console.error("[dsh-sharp-session] 停止会话失败:", error);
					}).finally(() => {
						cancelPending = false;
					});
				});
			};
			documentRoot.addEventListener("keydown", onKeyDown);
			return () => {
				documentRoot.removeEventListener("keydown", onKeyDown);
			};
		}
		//#endregion
		//#region src/client/switcher.ts
		/** 列表最多展示的条目数。 */
		const MAX_ITEMS = 12;
		function filterSessions(entries, query) {
			const needle = query.trim().toLowerCase();
			return (needle === "" ? entries : entries.filter(({ id, entry }) => displayName(id, entry).toLowerCase().includes(needle) || id.toLowerCase().includes(needle))).slice().sort((left, right) => {
				const lu = left.entry.updatedAt ?? 0;
				const ru = right.entry.updatedAt ?? 0;
				if (lu !== ru) return ru - lu;
				return left.id.localeCompare(right.id);
			}).slice(0, MAX_ITEMS);
		}
		function displayName(id, entry) {
			const title = entry.title?.trim();
			return title !== void 0 && title !== "" ? title : id.slice(0, 8);
		}
		function installSessionSwitcher(sessions, documentRoot = document) {
			let root;
			let input;
			let listEl;
			let selected = 0;
			let visible = [];
			const close = () => {
				if (root === void 0) return;
				root.remove();
				root = input = listEl = void 0;
			};
			const open = () => {
				if (root !== void 0) {
					input?.focus();
					return;
				}
				root = documentRoot.createElement("div");
				Object.assign(root.style, {
					position: "fixed",
					inset: "0",
					zIndex: "9999",
					background: "rgba(15, 18, 25, 0.45)",
					display: "flex",
					justifyContent: "center",
					alignItems: "flex-start",
					paddingTop: "14vh",
					fontFamily: "system-ui, sans-serif"
				});
				root.addEventListener("mousedown", (event) => {
					if (event.target === root) close();
				});
				const panel = documentRoot.createElement("div");
				Object.assign(panel.style, {
					width: "min(560px, 92vw)",
					borderRadius: "12px",
					overflow: "hidden",
					background: "rgb(24, 27, 36)",
					color: "rgb(232, 236, 245)",
					boxShadow: "0 18px 48px rgba(0, 0, 0, 0.45)"
				});
				input = documentRoot.createElement("input");
				input.type = "text";
				input.placeholder = "切换到会话…（↑↓ 选择，回车打开，Esc 关闭）";
				Object.assign(input.style, {
					width: "100%",
					boxSizing: "border-box",
					padding: "14px 16px",
					border: "none",
					outline: "none",
					fontSize: "15px",
					background: "transparent",
					color: "inherit",
					borderBottom: "1px solid rgba(255, 255, 255, 0.12)"
				});
				input.addEventListener("input", () => render(input.value));
				input.addEventListener("keydown", (event) => {
					if (event.key === "Escape") {
						event.preventDefault();
						close();
					} else if (event.key === "ArrowDown" || event.key === "ArrowUp") {
						event.preventDefault();
						if (visible.length === 0) return;
						selected = event.key === "ArrowDown" ? (selected + 1) % visible.length : (selected - 1 + visible.length) % visible.length;
						updateSelection();
					} else if (event.key === "Enter") {
						event.preventDefault();
						const target = visible[selected];
						if (target === void 0) return;
						close();
						try {
							sessions.open(target.id);
						} catch (error) {
							console.error("[dsh-sharp-session] 切换会话失败:", error);
						}
					}
				});
				listEl = documentRoot.createElement("div");
				Object.assign(listEl.style, {
					maxHeight: "46vh",
					overflowY: "auto"
				});
				panel.append(input, listEl);
				root.append(panel);
				documentRoot.body.append(root);
				render("");
				input.focus();
			};
			const updateSelection = () => {
				listEl?.querySelectorAll("[data-switcher-item]").forEach((node, index) => {
					const el = node;
					const active = index === selected;
					el.style.background = active ? "rgba(90, 130, 255, 0.25)" : "transparent";
					if (active) el.scrollIntoView?.({ block: "nearest" });
				});
			};
			const render = (query) => {
				if (listEl === void 0) return;
				const snapshot = sessions.list.getSnapshot();
				visible = filterSessions(Object.entries(snapshot.byId).filter((pair) => pair[1] !== void 0).map(([id, entry]) => ({
					id,
					entry
				})), query);
				selected = 0;
				listEl.replaceChildren();
				if (visible.length === 0) {
					const empty = documentRoot.createElement("div");
					empty.textContent = query.trim() === "" ? "（会话列表为空）" : "（无匹配会话）";
					Object.assign(empty.style, {
						padding: "16px",
						fontSize: "13px",
						opacity: "0.6"
					});
					listEl.append(empty);
					return;
				}
				for (const { id, entry } of visible) {
					const row = documentRoot.createElement("div");
					row.dataset.switcherItem = "";
					Object.assign(row.style, {
						display: "flex",
						alignItems: "center",
						gap: "8px",
						padding: "10px 16px",
						fontSize: "14px",
						cursor: "pointer"
					});
					const label = documentRoot.createElement("span");
					label.textContent = (entry.running ? "● " : "") + displayName(id, entry);
					label.style.flex = "1";
					label.style.overflow = "hidden";
					label.style.textOverflow = "ellipsis";
					label.style.whiteSpace = "nowrap";
					row.append(label);
					row.addEventListener("click", () => {
						close();
						try {
							sessions.open(id);
						} catch (error) {
							console.error("[dsh-sharp-session] 切换会话失败:", error);
						}
					});
					listEl.append(row);
				}
				updateSelection();
			};
			const onKeyDown = (event) => {
				if (event.repeat || event.isComposing) return;
				if (!(event.ctrlKey || event.metaKey) || event.key.toLowerCase() !== "k") return;
				event.preventDefault();
				event.stopPropagation();
				if (root === void 0) open();
				else close();
			};
			documentRoot.addEventListener("keydown", onKeyDown, true);
			return () => {
				close();
				documentRoot.removeEventListener("keydown", onKeyDown, true);
			};
		}
		//#endregion
		//#region src/client/theme-bridge.ts
		/**
		* 主题桥：把 DSH WebUI 的主题选择镜像给桌面壳。
		* web 端在 <html data-ds-theme-source> 上发布 light/dark/system，
		* 该属性本就是为宿主壳镜像设计的官方信号（官方 Electron 同样消费它）。
		* 经 WebView2 的 chrome.webview.postMessage 送达壳的 WebMessageReceived；
		* 普通浏览器中该通道不存在，静默降级为无操作。
		*/
		const THEME_SOURCE_ATTRIBUTE = "data-ds-theme-source";
		function installThemeBridge(documentRoot = document) {
			const post = (source) => {
				(window.chrome?.webview)?.postMessage?.(JSON.stringify({
					type: "dshsharp-theme",
					source
				}));
			};
			const readSource = () => documentRoot.documentElement.getAttribute("data-ds-theme-source") ?? "system";
			const observer = new MutationObserver(() => post(readSource()));
			observer.observe(documentRoot.documentElement, {
				attributes: true,
				attributeFilter: [THEME_SOURCE_ATTRIBUTE]
			});
			post(readSource());
			return () => observer.disconnect();
		}
		//#endregion
		//#region src/client/workspace-open.ts
		/**
		* 在系统文件管理器中打开一个工作区目录。
		*
		* DSH 没有客户端 `workspaces.openPath` 服务（该方法只存在于 Host 内部）；
		* 官方浏览器侧入口是 Remote RPC `session/openWorkspacePath`，经 Connection
		* carrier 调用，底层由 Host 按平台适配（Windows 资源管理器 / macOS Finder /
		* Linux 文件管理器）。省略 action 即“打开目录”语义（`reveal` 仅定位）。
		*/
		async function openWorkspacePath(connection, path) {
			const result = await connection.rpc.call("/api", "session/openWorkspacePath", { args: { request: { path } } });
			if (!result.ok) throw new Error(result.error.message || result.error.code);
		}
		//#endregion
		//#region src/client/ContextMenuView.tsx
		const COPY_SESSION_ITEM = {
			id: "copy-session-id",
			label: "复制会话 ID"
		};
		const OPEN_WORKSPACE_ITEM = {
			id: "open-workspace",
			label: "在资源管理器中打开",
			icon: /* @__PURE__ */ (0, react_jsx_runtime.jsx)(_deepseek_ai_dsh_client_ui_primitives.IconFolderOpen16, {})
		};
		/**
		* 复用 DSH 官方 Menu、sessions 和 workspaces 服务提供会话域右键动作。
		* 当前 DSH 没有行级菜单贡献插槽，因此只通过官方行的语义 ARIA 属性定位。
		*/
		function ContextMenuView({ useStore, actions, openWorkspace, getSessionSnapshot, getWorkspaceItems, features }) {
			const menu = useStore((state) => state);
			const actionsRef = (0, react.useRef)(actions);
			actionsRef.current = actions;
			(0, react.useEffect)(() => {
				const onContextMenu = (event) => {
					const target = event.target;
					if (!(target instanceof Element)) return;
					const workspaceRow = target.closest("[role=\"treeitem\"][aria-expanded]");
					if (workspaceRow instanceof HTMLElement && features.openWorkspace) {
						const label = workspaceRow.textContent?.trim() ?? "";
						const matches = getWorkspaceItems().filter((item) => workspaceLabel(item.path) === label);
						if (matches.length !== 1) return;
						event.preventDefault();
						actionsRef.current.openAt(event.clientX, event.clientY, {
							kind: "workspace",
							path: matches[0].path
						});
						return;
					}
					const sessionRow = target.closest("[role=\"treeitem\"][aria-selected]");
					if (!(sessionRow instanceof HTMLElement) || !features.copyId) return;
					if (sessionRow.getAttribute("aria-selected") !== "true") sessionRow.click();
					const sessionId = getSessionSnapshot().current;
					if (sessionId === void 0) return;
					event.preventDefault();
					actionsRef.current.openAt(event.clientX, event.clientY, {
						kind: "session",
						sessionId
					});
				};
				document.addEventListener("contextmenu", onContextMenu, true);
				return () => {
					document.removeEventListener("contextmenu", onContextMenu, true);
				};
			}, [getSessionSnapshot, getWorkspaceItems]);
			if (!menu.open || menu.target === null) return null;
			const item = menu.target.kind === "workspace" ? OPEN_WORKSPACE_ITEM : COPY_SESSION_ITEM;
			return /* @__PURE__ */ (0, react_jsx_runtime.jsx)(_deepseek_ai_dsh_client_ui_primitives.Menu, {
				open: true,
				portal: true,
				side: "bottom",
				getAnchorRect: () => new DOMRect(menu.x, menu.y, 0, 0),
				items: [item],
				onSelect: () => {
					const target = menu.target;
					actions.close();
					if (target.kind === "workspace") openWorkspace(target.path).catch((error) => {
						alert("打开工作区失败: " + String(error));
					});
					else navigator.clipboard.writeText(target.sessionId).catch((error) => {
						alert("复制会话 ID 失败: " + String(error));
					});
				},
				onClose: () => {
					actions.close();
				},
				anchor: /* @__PURE__ */ (0, react_jsx_runtime.jsx)("span", { "aria-hidden": "true" })
			});
		}
		function workspaceLabel(path) {
			const normalized = path.replace(/[\\/]+$/, "");
			return normalized.split(/[\\/]/).at(-1) ?? normalized;
		}
		//#endregion
		//#region src/client/menu-store.ts
		function createMenuStore() {
			return (0, _deepseek_ai_dsh_client_store.defineStore)({
				init: () => ({
					open: false,
					x: 0,
					y: 0,
					target: null
				}),
				actions: {
					openAt: (draft, x, y, target) => {
						draft.open = true;
						draft.x = x;
						draft.y = y;
						draft.target = target;
					},
					close: (draft) => {
						draft.open = false;
						draft.target = null;
					}
				}
			});
		}
		//#endregion
		//#region src/client/index.ts
		/** 所需服务：DSH 浏览器运行时的会话、连接、插槽与工作区服务。 */
		const inject = [
			"slots",
			"sessions",
			"workspaces",
			"connection"
		];
		/**
		* 注册浏览器端快捷键，并让监听器跟随插件生命周期卸载。
		* @param ctx DSH 浏览器客户端上下文。
		*/
		function apply(ctx) {
			const features = readFeatureFlags();
			ctx.effect(() => features.escStop ? installSessionShortcuts(ctx.sessions) : () => {}, "dsh-sharp-session: document keyboard listener");
			ctx.effect(() => features.trayNavigation ? installSessionNavigation(ctx.sessions) : () => {}, "dsh-sharp-session: tray session navigation");
			ctx.effect(() => installSessionSwitcher(ctx.sessions), "dsh-sharp-session: Ctrl+K session switcher");
			ctx.effect(() => installThemeBridge(), "dsh-sharp-session: theme bridge to host shell");
			ctx.slots.inject("shell.overlay", () => ctx.slots.register({
				name: "shell.overlay",
				id: "dsh-sharp-session.context-menu",
				order: 100,
				store: createMenuStore(),
				inject: () => ({
					openWorkspace: (path) => openWorkspacePath(ctx.connection, path),
					features,
					getSessionSnapshot: () => ctx.sessions.list.getSnapshot(),
					getWorkspaceItems: () => ctx.workspaces.list.getSnapshot().items
				})
			}, ContextMenuView));
		}
		//#endregion
		exports.apply = apply;
		exports.inject = inject;
		return module.exports;
	}
});

//# sourceMappingURL=client.js.map