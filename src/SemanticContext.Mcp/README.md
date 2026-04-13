# SemanticContext.Mcp

This project is the future MCP adapter boundary for SemanticContext.

Current MVP shape:
- Thin facade over `ICodeContextApplicationService`
- Manifest-backed repository/project metadata resource shapes
- No transport protocol implementation yet
- Ready for tool wiring such as `semantic_search`, `index_solution`, and `get_symbol_context`

Future work:
- Add the official MCP SDK adapter
- Map tool calls to the facade methods in this project
- Expose repository, project, and symbol resources with stable schemas
