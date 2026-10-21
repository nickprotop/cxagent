; Source: tree-sitter/tree-sitter-c-sharp @ f05a2ca99d329de2e6c32f26a21c6169b2bfcbb7, queries/tags.scm (MIT). Upstream, unchanged, down to
; the local block. Captures used: @name with @definition.*; references and the rest are ignored.

(class_declaration name: (identifier) @name) @definition.class

(class_declaration (base_list (_) @name)) @reference.class

(interface_declaration name: (identifier) @name) @definition.interface

(interface_declaration (base_list (_) @name)) @reference.interface

(method_declaration name: (identifier) @name) @definition.method

(object_creation_expression type: (identifier) @name) @reference.class

(type_parameter_constraints_clause (identifier) @name) @reference.class

(type_parameter_constraint (type type: (identifier) @name)) @reference.class

(variable_declaration type: (identifier) @name) @reference.class

(invocation_expression function: (member_access_expression name: (identifier) @name)) @reference.send

(namespace_declaration name: (identifier) @name) @definition.module

(namespace_declaration name: (identifier) @name) @module

; --- cxagent local additions ------------------------------------------------------------------
; Upstream finds classes, interfaces, methods and namespaces only; C# declares much more.
(struct_declaration name: (identifier) @name) @definition.class
(record_declaration name: (identifier) @name) @definition.class
(enum_declaration name: (identifier) @name) @definition.class
(delegate_declaration name: (identifier) @name) @definition.type
(constructor_declaration name: (identifier) @name) @definition.method
(property_declaration name: (identifier) @name) @definition.method
(global_statement (local_function_statement name: (identifier) @name) @definition.function)
; Operators, conversions, indexers and destructors: members with no name of their own, so the
; keyword stands in for one.
(operator_declaration "operator" @name) @definition.method
(conversion_operator_declaration "operator" @name) @definition.method
(indexer_declaration "this" @name) @definition.method
(destructor_declaration name: (identifier) @name) @definition.method
