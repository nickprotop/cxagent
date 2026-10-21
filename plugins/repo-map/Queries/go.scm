; Source: tree-sitter/tree-sitter-go @ 2346a3ab1bb3857b48b29d779a1ef9799a248cd7, queries/tags.scm (MIT). Upstream, unchanged, down to
; the local block. Captures used: @name with @definition.*; references and the rest are ignored.

(
  (comment)* @doc
  .
  (function_declaration
    name: (identifier) @name) @definition.function
  (#strip! @doc "^//\\s*")
  (#set-adjacent! @doc @definition.function)
)

(
  (comment)* @doc
  .
  (method_declaration
    name: (field_identifier) @name) @definition.method
  (#strip! @doc "^//\\s*")
  (#set-adjacent! @doc @definition.method)
)

(call_expression
  function: [
    (identifier) @name
    (parenthesized_expression (identifier) @name)
    (selector_expression field: (field_identifier) @name)
    (parenthesized_expression (selector_expression field: (field_identifier) @name))
  ]) @reference.call

(type_spec
  name: (type_identifier) @name) @definition.type

(type_identifier) @name @reference.type

(package_clause "package" (package_identifier) @name)

(type_declaration (type_spec name: (type_identifier) @name type: (interface_type)))

(type_declaration (type_spec name: (type_identifier) @name type: (struct_type)))

(import_declaration (import_spec) @name)

(var_declaration (var_spec name: (identifier) @name))

(const_declaration (const_spec name: (identifier) @name))

; --- cxagent local additions ------------------------------------------------------------------
; Upstream lists an interface but not the methods it declares.
(method_elem name: (field_identifier) @name) @definition.method
; A type alias, which upstream's type_spec pattern does not match.
(type_alias name: (type_identifier) @name) @definition.type
