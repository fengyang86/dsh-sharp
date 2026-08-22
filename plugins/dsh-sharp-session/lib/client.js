window.__ModuleLoader__.load({
	id: "@yangfeng/dsh-sharp-session",
	factory: (require) => {
		var module = { exports: {} };
		var exports = module.exports;
		Object.defineProperty(exports, Symbol.toStringTag, { value: "Module" });
		let react = require("react");
		let _deepseek_ai_dsh_client_ui_primitives = require("@deepseek-ai/dsh-client-ui-primitives");
		let react_jsx_runtime = require("react/jsx-runtime");
		let _deepseek_ai_dsh_client_runtime_client = require("@deepseek-ai/dsh-client-runtime/client");
		//#region src/client/shortcut.ts
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
		function ContextMenuView({ useStore, actions, useSessions, useWorkspaces, openWorkspace }) {
			const menu = useStore((state) => state);
			const sessions = useSessions((state) => state);
			const workspaces = useWorkspaces((state) => state.items);
			const actionsRef = (0, react.useRef)(actions);
			actionsRef.current = actions;
			(0, react.useEffect)(() => {
				const onContextMenu = (event) => {
					const target = event.target;
					if (!(target instanceof Element)) return;
					const workspaceRow = target.closest("[role=\"treeitem\"][aria-expanded]");
					if (workspaceRow instanceof HTMLElement) {
						const text = workspaceRow.textContent?.trim() ?? "";
						const workspace = workspaces.find((item) => text.startsWith(item.title));
						if (workspace === void 0) return;
						event.preventDefault();
						actionsRef.current.openAt(event.clientX, event.clientY, {
							kind: "workspace",
							path: workspace.path
						});
						return;
					}
					const sessionRow = target.closest("[role=\"treeitem\"][aria-selected]");
					if (!(sessionRow instanceof HTMLElement)) return;
					const selected = sessionRow.getAttribute("aria-selected") === "true";
					const current = sessions.current;
					const text = sessionRow.textContent?.trim() ?? "";
					const ids = sessions.ids ?? Object.keys(sessions.byId);
					const sessionId = selected && current !== void 0 ? current : ids.find((id) => {
						const session = sessions.byId[id];
						return session !== void 0 && text.startsWith(session.title ?? "");
					});
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
			}, [sessions, workspaces]);
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
		//#endregion
		//#region src/client/menu-store.ts
		function createMenuStore() {
			return (0, _deepseek_ai_dsh_client_runtime_client.defineStore)({
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
			ctx.effect(() => installSessionShortcuts(ctx.sessions), "dsh-sharp-session: document keyboard listener");
			ctx.slots.inject("shell.overlay", () => ctx.slots.register({
				name: "shell.overlay",
				id: "dsh-sharp-session.context-menu",
				order: 100,
				store: createMenuStore(),
				inject: () => ({ openWorkspace: (path) => ctx.workspaces.openPath(path) })
			}, ContextMenuView));
		}
		//#endregion
		exports.apply = apply;
		exports.inject = inject;
		return module.exports;
	}
});

//# sourceMappingURL=client.js.map