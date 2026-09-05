# Horizun Revit MCP 1.2.1

This patch adds ChatGPT Work through OpenAI's Secure MCP Tunnel. The Windows
installer provides setup, status, stop and diagnostic entry points while every
client continues to use the same installed MCP server.

It also removes organisation-specific classification skills and adds a release
gate that prevents their terminology from returning in current product files.

Install `horizun-mcp-1.2.1-setup.exe`, verify it with `SHA256SUMS.txt`, close
Revit and run Setup. See [CLIENTS.md](CLIENTS.md) for each client's last step.
