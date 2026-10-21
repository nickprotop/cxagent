; Source: tree-sitter/tree-sitter-php @ 7d07b41ce2d442ca9a90ed85d0075eccc17ae315, queries/tags.scm (MIT). Upstream, unchanged, down to
; the local block. Captures used: @name with @definition.* or @reference.*; the rest is ignored.

(namespace_definition
  name: (namespace_name) @name) @definition.module

(interface_declaration
  name: (name) @name) @definition.interface

(trait_declaration
  name: (name) @name) @definition.interface

(class_declaration
  name: (name) @name) @definition.class

(class_interface_clause [(name) (qualified_name)] @name) @reference.implementation

(property_declaration
  (property_element (variable_name (name) @name))) @definition.field

(function_definition
  name: (name) @name) @definition.function

(method_declaration
  name: (name) @name) @definition.function

(object_creation_expression
  [
    (qualified_name (name) @name)
    (variable_name (name) @name)
  ]) @reference.class

(function_call_expression
  function: [
    (qualified_name (name) @name)
    (variable_name (name)) @name
  ]) @reference.call

(scoped_call_expression
  name: (name) @name) @reference.call

(member_call_expression
  name: (name) @name) @reference.call

; --- cxagent local additions ------------------------------------------------------------------
; Upstream misses enums, type declarations, parents and static calls.
(named_type (name) @name) @reference.type
(base_clause (name) @name) @reference.class
(scoped_call_expression scope: (name) @name) @reference.type
(enum_declaration name: (name) @name) @definition.class
