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
		/** 所需服务：DSH 浏览器运行时的会话服务。 */
		const inject = [
			"slots",
			"sessions",
			"workspaces"
		];
		/**
		* 注册浏览器端快捷键，并让监听器跟随插件生命周期卸载。
		* @param ctx DSH 浏览器客户端上下文。
		*/
		function apply(ctx) {
			const features = readFeatureFlags();
			ctx.effect(() => features.escStop ? installSessionShortcuts(ctx.sessions) : () => {}, "dsh-sharp-session: document keyboard listener");
			ctx.effect(() => features.trayNavigation ? installSessionNavigation(ctx.sessions) : () => {}, "dsh-sharp-session: tray session navigation");
			ctx.slots.inject("shell.overlay", () => ctx.slots.register({
				name: "shell.overlay",
				id: "dsh-sharp-session.context-menu",
				order: 100,
				store: createMenuStore(),
				inject: () => ({
					openWorkspace: (path) => ctx.workspaces.openPath(path),
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