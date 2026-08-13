import { spawn } from "node:child_process";

export const CmuxWindowsStatus = async (context) => {
  const active = new Set();
  const cwdBySession = new Map();

  const report = (sessionID, state, notify = false, cwd = null) => {
    const gui = process.env.CMUX_GUI_EXE;
    const terminal = process.env.CMUX_TERMINAL_ID;
    if (!gui || !terminal || !sessionID) return;
    const child = spawn(gui, ["--agent-hook", "opencode", terminal], {
      windowsHide: true,
      stdio: ["pipe", "ignore", "ignore"],
    });
    child.on("error", () => {});
    child.stdin.end(JSON.stringify({
      session_id: sessionID,
      state,
      cwd: cwd || cwdBySession.get(sessionID) || context.directory,
      notify,
      resumable: state !== "unknown",
    }));
    child.unref();
  };

  return {
    event: async ({ event }) => {
      const properties = event.properties || {};
      switch (event.type) {
        case "session.created": {
          const info = properties.info || {};
          if (!info.id) return;
          cwdBySession.set(info.id, info.directory || context.directory);
          report(info.id, "idle", false, info.directory);
          break;
        }
        case "session.status": {
          const sessionID = properties.sessionID;
          const status = properties.status?.type;
          if (!sessionID || !status) return;
          if (status === "busy") {
            active.add(sessionID);
            report(sessionID, "working");
          } else if (status === "retry") {
            active.add(sessionID);
            report(sessionID, "blocked", true);
          } else if (status === "idle") {
            const completed = active.delete(sessionID);
            report(sessionID, completed ? "done" : "idle", completed);
          }
          break;
        }
        case "permission.asked":
        case "question.asked": {
          const sessionID = properties.sessionID;
          if (sessionID) report(sessionID, "blocked", true);
          break;
        }
        case "session.error": {
          const sessionID = properties.sessionID;
          if (sessionID) report(sessionID, "blocked", true);
          break;
        }
        case "session.deleted": {
          const sessionID = properties.info?.id;
          if (!sessionID) return;
          active.delete(sessionID);
          report(sessionID, "unknown", false);
          cwdBySession.delete(sessionID);
          break;
        }
      }
    },
  };
};
