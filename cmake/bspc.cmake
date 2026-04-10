# BSPC - BSP to AAS compiler
# Generates bot navigation files from Quake III BSP maps
# Source lives in code/bspc/ submodule (TTimo/bspc)

set(BSPC_DIR "${CMAKE_SOURCE_DIR}/code/bspc")

set(BSPC_SOURCES
    ${BSPC_DIR}/_files.c
    ${BSPC_DIR}/aas_areamerging.c
    ${BSPC_DIR}/aas_cfg.c
    ${BSPC_DIR}/aas_create.c
    ${BSPC_DIR}/aas_edgemelting.c
    ${BSPC_DIR}/aas_facemerging.c
    ${BSPC_DIR}/aas_file.c
    ${BSPC_DIR}/aas_gsubdiv.c
    ${BSPC_DIR}/aas_map.c
    ${BSPC_DIR}/aas_prunenodes.c
    ${BSPC_DIR}/aas_store.c
    ${BSPC_DIR}/be_aas_bspc.c
    ${BSPC_DIR}/brushbsp.c
    ${BSPC_DIR}/bspc.c
    ${BSPC_DIR}/csg.c
    ${BSPC_DIR}/glfile.c
    ${BSPC_DIR}/l_bsp_ent.c
    ${BSPC_DIR}/l_bsp_hl.c
    ${BSPC_DIR}/l_bsp_q1.c
    ${BSPC_DIR}/l_bsp_q2.c
    ${BSPC_DIR}/l_bsp_q3.c
    ${BSPC_DIR}/l_bsp_sin.c
    ${BSPC_DIR}/l_cmd.c
    ${BSPC_DIR}/l_log.c
    ${BSPC_DIR}/l_math.c
    ${BSPC_DIR}/l_mem.c
    ${BSPC_DIR}/l_poly.c
    ${BSPC_DIR}/l_qfiles.c
    ${BSPC_DIR}/l_threads.c
    ${BSPC_DIR}/l_utils.c
    ${BSPC_DIR}/leakfile.c
    ${BSPC_DIR}/map.c
    ${BSPC_DIR}/map_hl.c
    ${BSPC_DIR}/map_q1.c
    ${BSPC_DIR}/map_q2.c
    ${BSPC_DIR}/map_q3.c
    ${BSPC_DIR}/map_sin.c
    ${BSPC_DIR}/nodraw.c
    ${BSPC_DIR}/portals.c
    ${BSPC_DIR}/textures.c
    ${BSPC_DIR}/tree.c

    # Engine deps (BSPC's own bundled copies)
    ${BSPC_DIR}/deps/qcommon/unzip.c
    ${BSPC_DIR}/deps/qcommon/cm_load.c
    ${BSPC_DIR}/deps/qcommon/cm_patch.c
    ${BSPC_DIR}/deps/qcommon/cm_test.c
    ${BSPC_DIR}/deps/qcommon/cm_trace.c
    ${BSPC_DIR}/deps/qcommon/md4.c
    ${BSPC_DIR}/deps/botlib/be_aas_bspq3.c
    ${BSPC_DIR}/deps/botlib/be_aas_cluster.c
    ${BSPC_DIR}/deps/botlib/be_aas_move.c
    ${BSPC_DIR}/deps/botlib/be_aas_optimize.c
    ${BSPC_DIR}/deps/botlib/be_aas_reach.c
    ${BSPC_DIR}/deps/botlib/be_aas_sample.c
    ${BSPC_DIR}/deps/botlib/l_libvar.c
    ${BSPC_DIR}/deps/botlib/l_precomp.c
    ${BSPC_DIR}/deps/botlib/l_script.c
    ${BSPC_DIR}/deps/botlib/l_struct.c
)

add_executable(bspc ${BSPC_SOURCES})

target_include_directories(bspc PRIVATE
    ${BSPC_DIR}
    ${BSPC_DIR}/deps
)

target_compile_definitions(bspc PRIVATE
    BSPC
    _CRT_SECURE_NO_WARNINGS
    _CRT_SECURE_NO_DEPRECATE
    $<$<PLATFORM_ID:Windows>:WIN32>
    $<$<NOT:$<PLATFORM_ID:Windows>>:LINUX stricmp=strcasecmp Com_Memcpy=memcpy Com_Memset=memset MAC_STATIC= QDECL=>
)

# BSPC's old q_platform.h doesn't detect MSVC x64 properly (__WIN64__ vs _M_X64)
if(MSVC AND CMAKE_SIZEOF_VOID_P EQUAL 8)
    target_compile_definitions(bspc PRIVATE __WIN64__)
endif()

# Suppress warnings in third-party code
if(MSVC)
    target_compile_options(bspc PRIVATE /W1 /wd4996 /wd4267 /wd4244 /wd4018 /wd4273)
else()
    target_compile_options(bspc PRIVATE -w)
endif()

if(NOT WIN32)
    target_link_libraries(bspc PRIVATE m pthread)
endif()

# Output bspc.exe alongside the game executable
include(utils/set_output_dirs)
set_output_dirs(bspc)
