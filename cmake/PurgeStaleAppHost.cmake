# ---------------------------------------------------------------------------
# Work around a .NET SDK incremental-build gap.
#
# The apphost (obj/.../apphost.exe) is the native stub that gets stamped with the
# assembly name, the Win32 manifest and the application icon, and is then copied
# out as <App>.exe.  The SDK does not list app.manifest or the .ico among that
# step's inputs, so editing either rebuilds everything *except* the apphost - and
# the shipped .exe silently keeps the previous manifest/icon.  When the manifest is
# wrong the app dies at startup with "the side-by-side configuration is incorrect"
# (SXS 14001) while every rebuild looks perfectly clean.
#
# So: delete any apphost older than those inputs and let the SDK make a fresh one.
#
# Invoked as:
#   cmake -DSRC_DIR=<src> -DROOT_DIR=<repo root> -DOBJ_ROOT=<build/obj> \
#         -P PurgeStaleAppHost.cmake
# ---------------------------------------------------------------------------

if(NOT DEFINED SRC_DIR)
    message(FATAL_ERROR "PurgeStaleAppHost.cmake requires -DSRC_DIR=<path>")
endif()
if(NOT DEFINED ROOT_DIR)
    set(ROOT_DIR "${SRC_DIR}/..")
endif()
if(NOT DEFINED OBJ_ROOT)
    set(OBJ_ROOT "${ROOT_DIR}/build/obj")
endif()

# Everything the apphost stamping step consumes but does not declare as an input.
set(_watched "")
if(EXISTS "${SRC_DIR}/app.manifest")
    list(APPEND _watched "${SRC_DIR}/app.manifest")
endif()

file(GLOB _icons
     "${ROOT_DIR}/assets/*.ico"
     "${SRC_DIR}/Assets/*.ico"
     "${ROOT_DIR}/*.ico")          # legacy layout, harmless once empty
list(APPEND _watched ${_icons})

if(NOT _watched)
    return()
endif()

# Both the current location (Directory.Build.props redirects obj\ here) and the SDK
# default next to the project, so the guard still works if that props file is removed.
file(GLOB_RECURSE _apphosts
     "${OBJ_ROOT}/*/apphost.exe"
     "${SRC_DIR}/obj/*/apphost.exe")

foreach(_apphost IN LISTS _apphosts)
    foreach(_input IN LISTS _watched)
        if("${_input}" IS_NEWER_THAN "${_apphost}")
            get_filename_component(_name "${_input}" NAME)
            message(STATUS "${_name} changed - removing stale apphost: ${_apphost}")
            file(REMOVE "${_apphost}")
            break()
        endif()
    endforeach()
endforeach()
