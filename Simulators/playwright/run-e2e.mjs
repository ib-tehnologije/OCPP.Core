import { spawn } from "node:child_process";
import { once } from "node:events";
import { packageDir } from "./common.mjs";

const browserOnly = process.argv.includes("--browser-only");
const stack = spawn("node", ["./stack.mjs"], {
  cwd: packageDir,
  env: {
    ...process.env,
  },
  stdio: ["ignore", "pipe", "pipe"],
});

let ready = false;
const stackLogs = [];

stack.stdout.on("data", (chunk) => {
  const text = chunk.toString();
  stackLogs.push(text);
  process.stdout.write(text);
  if (text.includes("OCPP_PLAYWRIGHT_STACK_READY")) {
    ready = true;
  }
});

stack.stderr.on("data", (chunk) => {
  const text = chunk.toString();
  stackLogs.push(text);
  process.stderr.write(text);
});

stack.on("exit", (code) => {
  if (!ready) {
    console.error(`Stack failed before becoming ready. Exit code: ${code ?? -1}`);
    process.exit(code ?? 1);
  }
});

while (!ready) {
  await new Promise((resolve) => setTimeout(resolve, 250));
}

const runCommand = (command, args) =>
  new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd: packageDir,
      env: {
        ...process.env,
        OCPP_PLAYWRIGHT_USE_EXISTING_STACK: "1",
      },
      stdio: "inherit",
    });

    child.on("exit", (code) => {
      if (code === 0) {
        resolve();
      } else {
        reject(new Error(`${command} ${args.join(" ")} failed with code ${code ?? -1}`));
      }
    });
  });

try {
  if (!browserOnly) {
    await runCommand("node", ["./runner.mjs", "--mode", "matrix"]);
  }

  await runCommand("npx", ["playwright", "test"]);
} finally {
  stack.kill("SIGTERM");
  await Promise.race([
    once(stack, "exit"),
    new Promise((resolve) => setTimeout(resolve, 3_000)),
  ]);
}
