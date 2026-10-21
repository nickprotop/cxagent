; Source: tree-sitter/tree-sitter-python @ 26855eabccb19c6abf499fbc5b8dc7cc9ab8bc64, queries/tags.scm (MIT). Upstream, unchanged, down to
; the local block. Captures used: @name with @definition.*; references and the rest are ignored.

(module (expression_statement (assignment left: (identifier) @name) @definition.constant))

(class_definition
  name: (identifier) @name) @definition.class

(function_definition
  name: (identifier) @name) @definition.function

(call
  function: [
      (identifier) @name
      (attribute
        attribute: (identifier) @name)
  ]) @reference.call
