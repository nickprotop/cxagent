# cxp.cmake — build a cxagent plugin with the C SDK.
#
#   include(cxp/cxp.cmake)
#   cxp_add_plugin(<target> SIDECAR <name>.plugin.json SOURCES <your .c files>)
#
# A shared library implementing the cxagent plugin ABI, built so the sidecar is the one truth:
#   - the sidecar is embedded as describe's answer;
#   - its "pluginContract" is the handshake;
#   - poll is compiled in only with "client": true, and command only when "commands" lists any —
#     cxagent polls any library that exports poll, so exporting it unasked costs every plugin a timer;
#   - the library is named after the sidecar and the sidecar copied beside it, because cxagent finds
#     one from the other.
#
# VENDORED: cJSON 1.7.18 (MIT, see cJSON-LICENSE), unmodified, from github.com/DaveGamble/cJSON tag v1.7.18.
#   cJSON.c  sha256 75c51de8fa40ac9d7a99319c6330719bd692eb81c0a869265f3d4c682533f9b9
#   cJSON.h  sha256 0578cc29132912edbc88f83207a8fc76e5db3db0605497e909a9384ef3cc474b

cmake_minimum_required(VERSION 3.20)
set(CXP_DIR "${CMAKE_CURRENT_LIST_DIR}")

function(cxp_add_plugin target)
  cmake_parse_arguments(CXP "" "SIDECAR" "SOURCES" ${ARGN})
  if(NOT CXP_SIDECAR)
    message(FATAL_ERROR "cxp_add_plugin(${target}): SIDECAR <name>.plugin.json is required.")
  endif()
  get_filename_component(sidecar "${CXP_SIDECAR}" ABSOLUTE)
  if(NOT EXISTS "${sidecar}")
    message(FATAL_ERROR "cxp_add_plugin(${target}): no sidecar at ${sidecar}.")
  endif()

  # A SIDECAR THIS CANNOT READ FAILS THE CONFIGURE, naming the file: a library built from it would
  # describe nothing cxagent can load.
  file(READ "${sidecar}" json)
  string(JSON contract ERROR_VARIABLE err GET "${json}" pluginContract)
  if(err)
    message(FATAL_ERROR "${sidecar}: no readable \"pluginContract\" — ${err}")
  endif()
  string(JSON client ERROR_VARIABLE noclient GET "${json}" client)
  string(JSON commands ERROR_VARIABLE nocommands LENGTH "${json}" commands)

  # THE SIDECAR AS ONE C STRING LITERAL: backslashes, then quotes, escaped; each line its own
  # adjacent literal so a long sidecar stays within any compiler's line limits.
  string(REPLACE "\\" "\\\\" escaped "${json}")
  string(REPLACE "\"" "\\\"" escaped "${escaped}")
  string(REPLACE "\r" "" escaped "${escaped}")
  string(REPLACE "\n" "\\n\"\n\"" escaped "${escaped}")
  set(generated "${CMAKE_CURRENT_BINARY_DIR}/${target}-cxp")
  file(WRITE "${generated}/cxp_manifest.h.new" "static const char CXP_MANIFEST[] =\n\"${escaped}\";\n")
  file(COPY_FILE "${generated}/cxp_manifest.h.new" "${generated}/cxp_manifest.h" ONLY_IF_DIFFERENT)

  add_library(${target} SHARED ${CXP_SOURCES} "${CXP_DIR}/cxp.c" "${CXP_DIR}/cJSON.c")
  target_include_directories(${target} PRIVATE "${CXP_DIR}" "${generated}")
  target_compile_definitions(${target} PRIVATE CXP_CONTRACT=${contract})
  if(NOT noclient AND client)
    target_compile_definitions(${target} PRIVATE CXP_CLIENT)
  endif()
  if(NOT nocommands AND commands GREATER 0)
    target_compile_definitions(${target} PRIVATE CXP_COMMANDS)
  endif()

  # NAMED AFTER THE SIDECAR, no "lib" prefix: weather.plugin.json beside weather.so, as the loader pairs them.
  get_filename_component(stem "${sidecar}" NAME)
  string(REGEX REPLACE "\\.plugin\\.json$" "" stem "${stem}")
  set_target_properties(${target} PROPERTIES
    OUTPUT_NAME "${stem}"
    PREFIX ""
    C_STANDARD 11
    C_VISIBILITY_PRESET hidden)
  find_package(Threads REQUIRED)
  target_link_libraries(${target} PRIVATE Threads::Threads)

  add_custom_command(TARGET ${target} POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different "${sidecar}" "$<TARGET_FILE_DIR:${target}>/${stem}.plugin.json"
    VERBATIM)
  set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${sidecar}")
endfunction()
