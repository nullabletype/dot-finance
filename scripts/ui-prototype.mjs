import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import path from "node:path";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const prototypeFile = path.join(root, "prototypes", "DesktopShell", "ui-prototype", "index.html");
const host = "127.0.0.1";
const requestedPort = Number.parseInt(process.env.DOT_FINANCE_UI_PORT ?? "4178", 10);

const server = createServer(async (request, response) => {
  if (request.url === "/favicon.ico") {
    response.writeHead(204).end();
    return;
  }

  try {
    const html = await readFile(prototypeFile);
    response.writeHead(200, {
      "Content-Type": "text/html; charset=utf-8",
      "Cache-Control": "no-store",
    });
    response.end(html);
  } catch (error) {
    response.writeHead(500, { "Content-Type": "text/plain; charset=utf-8" });
    response.end(`Could not read the UI prototype.\n${error.message}\n`);
  }
});
server.listen(requestedPort, host, () => {
  const address = server.address();
  const url = `http://${host}:${address.port}/?variant=a`;
  console.log(`dot-finance UI prototype: ${url}`);
  console.log("Press Ctrl+C to stop.");

  if (!process.argv.includes("--no-open")) {
    const command = process.platform === "darwin" ? "open" : process.platform === "win32" ? "cmd" : "xdg-open";
    const args = process.platform === "win32" ? ["/c", "start", "", url] : [url];
    spawn(command, args, { detached: true, stdio: "ignore" }).unref();
  }
});
