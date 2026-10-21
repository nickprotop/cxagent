; Source: tree-sitter/tree-sitter-c @ ae19b676b13bdcc13b7665397e6d9b14975473dd, queries/tags.scm (MIT). Upstream, unchanged, down to
; the local block. Captures used: @name with @definition.*; references and the rest are ignored.

(struct_specifier name: (type_identifier) @name body:(_)) @definition.class

(declaration type: (union_specifier name: (type_identifier) @name)) @definition.class

(function_declarator declarator: (identifier) @name) @definition.function

(type_definition declarator: (type_identifier) @name) @definition.type

(enum_specifier name: (type_identifier) @name) @definition.type
