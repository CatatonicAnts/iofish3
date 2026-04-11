if(NOT USE_FREETYPE)
    return()
endif()

# Build FreeType from submodule source
set(FREETYPE_SOURCE_DIR "${CMAKE_SOURCE_DIR}/code/libs/freetype")

if(EXISTS "${FREETYPE_SOURCE_DIR}/CMakeLists.txt")
    # Disable optional FreeType dependencies we don't need
    set(FT_DISABLE_BZIP2 ON CACHE BOOL "" FORCE)
    set(FT_DISABLE_BROTLI ON CACHE BOOL "" FORCE)
    set(FT_DISABLE_HARFBUZZ ON CACHE BOOL "" FORCE)
    set(FT_DISABLE_PNG ON CACHE BOOL "" FORCE)
    set(FT_DISABLE_ZLIB ON CACHE BOOL "" FORCE)
    set(SKIP_INSTALL_ALL ON CACHE BOOL "" FORCE)

    # Build as shared library so the .NET renderer can P/Invoke into it
    set(BUILD_SHARED_LIBS_SAVED ${BUILD_SHARED_LIBS})
    set(BUILD_SHARED_LIBS ON)

    add_subdirectory("${FREETYPE_SOURCE_DIR}" "${CMAKE_BINARY_DIR}/freetype")

    set(BUILD_SHARED_LIBS ${BUILD_SHARED_LIBS_SAVED})

    # Place freetype DLL in the game directory alongside the engine
    if(GAME_DIR)
        set_target_properties(freetype PROPERTIES
            RUNTIME_OUTPUT_DIRECTORY "${GAME_DIR}"
            RUNTIME_OUTPUT_DIRECTORY_DEBUG "${GAME_DIR}"
            RUNTIME_OUTPUT_DIRECTORY_RELEASE "${GAME_DIR}"
            RUNTIME_OUTPUT_DIRECTORY_RELWITHDEBINFO "${GAME_DIR}"
            RUNTIME_OUTPUT_DIRECTORY_MINSIZEREL "${GAME_DIR}"
            LIBRARY_OUTPUT_DIRECTORY "${GAME_DIR}"
            LIBRARY_OUTPUT_DIRECTORY_DEBUG "${GAME_DIR}"
            LIBRARY_OUTPUT_DIRECTORY_RELEASE "${GAME_DIR}"
            LIBRARY_OUTPUT_DIRECTORY_RELWITHDEBINFO "${GAME_DIR}"
            LIBRARY_OUTPUT_DIRECTORY_MINSIZEREL "${GAME_DIR}")
    endif()

    if(BUILD_RENDERER_GL1 OR BUILD_RENDERER_GL2)
        list(APPEND RENDERER_INCLUDE_DIRS "${FREETYPE_SOURCE_DIR}/include")
        list(APPEND RENDERER_LIBRARIES freetype)
    endif()
else()
    # Fallback to system-installed FreeType
    find_package(Freetype REQUIRED)
    list(APPEND RENDERER_INCLUDE_DIRS ${FREETYPE_INCLUDE_DIRS})
    list(APPEND RENDERER_LIBRARIES ${FREETYPE_LIBRARIES})
endif()
