; Source: tree-sitter/tree-sitter-c @ ae19b676b13bdcc13b7665397e6d9b14975473dd, queries/tags.scm (MIT). Upstream, unchanged, down to
; the local block. Captures used: @name with @definition.* or @reference.*; the rest is ignored.

(struct_specifier name: (type_identifier) @name body:(_)) @definition.class

(declaration type: (union_specifier name: (type_identifier) @name)) @definition.class

(function_declarator declarator: (identifier) @name) @definition.function

(type_definition declarator: (type_identifier) @name) @definition.type

(enum_specifier name: (type_identifier) @name) @definition.type

; --- cxagent local additions ------------------------------------------------------------------
; Upstream marks no references at all; calls and type names are what link a C file to another.
(call_expression function: (identifier) @name) @reference.call
(call_expression function: (field_expression field: (field_identifier) @name)) @reference.call
((type_identifier) @name @reference.type)
