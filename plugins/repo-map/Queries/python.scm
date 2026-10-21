; Source: tree-sitter/tree-sitter-python @ 26855eabccb19c6abf499fbc5b8dc7cc9ab8bc64, queries/tags.scm (MIT). Upstream, unchanged, down to
; the local block. Captures used: @name with @definition.* or @reference.*; the rest is ignored.

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

; --- cxagent local additions ------------------------------------------------------------------
; Upstream marks only calls; type hints, base classes, decorators and imports are how Python files use each other's names.
(type (identifier) @name) @reference.type
(type (generic_type (identifier) @name)) @reference.type
(type (attribute attribute: (identifier) @name)) @reference.type
(class_definition superclasses: (argument_list (identifier) @name)) @reference.class
(class_definition superclasses: (argument_list (attribute attribute: (identifier) @name))) @reference.class
(decorator (identifier) @name) @reference.call
(import_from_statement name: (dotted_name (identifier) @name)) @reference.import
(import_from_statement name: (aliased_import name: (dotted_name (identifier) @name))) @reference.import
