# SemanticContext.Mcp

This project contains the local MCP server for SemanticContext.

Current shape:
- Thin facade over `ICodeContextApplicationService`
- Stdio MCP host built on the official C# MCP SDK
- Tool definitions for `semantic_search`, `index_solution`, and `get_symbol_context`
- Resource definitions for repository, project, and symbol context
- No duplicated indexing or retrieval logic

Future work:
- Add optional HTTP transport if we want the same MCP surface over HTTP
- Add prompts for explain-this-area workflows
- Add richer paging and resource discovery helpers
