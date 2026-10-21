; Source: tree-sitter/tree-sitter-cpp @ 12bd6f7e96080d2e70ec51d4068f2f66120dde35, queries/tags.scm (MIT). Upstream, unchanged, down to
; the local block. Captures used: @name with @definition.*; references and the rest are ignored.

(struct_specifier name: (type_identifier) @name body:(_)) @definition.class

(declaration type: (union_specifier name: (type_identifier) @name)) @definition.class

(function_declarator declarator: (identifier) @name) @definition.function

(function_declarator declarator: (field_identifier) @name) @definition.function

(function_declarator declarator: (qualified_identifier scope: (namespace_identifier) @local.scope name: (identifier) @name)) @definition.method

(type_definition declarator: (type_identifier) @name) @definition.type

(enum_specifier name: (type_identifier) @name) @definition.type

(class_specifier name: (type_identifier) @name) @definition.class

; --- cxagent local additions ------------------------------------------------------------------
; Operators, destructors and explicit specialisations, which upstream's identifier-only pattern misses.
(function_declarator declarator: (operator_name) @name) @definition.function
(function_declarator declarator: (destructor_name) @name) @definition.function
(function_declarator declarator: (template_function name: (identifier) @name)) @definition.function
