cmake_minimum_required(VERSION 3.20)
project(CXP_NAME C)

# THE C SDK, copied into cxp/ by `cxagent plugin new`: one call builds the plugin from its sidecar.
include(cxp/cxp.cmake)
cxp_add_plugin(CXP_NAME SIDECAR CXP_NAME.plugin.json SOURCES CXP_NAME.c)

# THE README BESIDE THE LIBRARY TOO, so the build folder is the whole plugin `cxagent plugin pack` zips.
add_custom_command(TARGET CXP_NAME POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E copy_if_different ${CMAKE_CURRENT_SOURCE_DIR}/README.md $<TARGET_FILE_DIR:CXP_NAME>/README.md
  VERBATIM)

# `ctest` RUNS THE PLUGIN ONCE THROUGH cxagent, as a session would — skipped where cxagent is not installed.
find_program(CXAGENT cxagent)
if(CXAGENT)
  enable_testing()
  add_test(NAME count_this_file
           COMMAND ${CXAGENT} plugin call $<TARGET_FILE_DIR:CXP_NAME> CXP_NAME_count "{\"file\":\"CMakeLists.txt\"}"
                   --cwd ${CMAKE_CURRENT_SOURCE_DIR} --answer allow)
endif()
