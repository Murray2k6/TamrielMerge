# TamrielMergeNative.dll: an SKSE plugin built against SKSE 2.3.1's own source (runtime 1.7.104; the game folder's
# src\skse64-2.3.1.tar.gz, extracted to ..\third_party) and ianpatt's common library (zlib; ..\third_party\common,
# installed to ..\third_party\extern). build.ps1 runs it.
cmake_minimum_required(VERSION 3.18)
project(TamrielMergeNative LANGUAGES CXX)

set(SKSE_ROOT ${CMAKE_CURRENT_SOURCE_DIR}/../third_party/skse64-2.3.1)
list(APPEND CMAKE_PREFIX_PATH ${CMAKE_CURRENT_SOURCE_DIR}/../third_party/extern)
find_package(common REQUIRED CONFIG)
add_subdirectory(${SKSE_ROOT}/skse64_common skse64_common)

include(${SKSE_ROOT}/cmake/versioning.cmake)

# The parts of SKSE's game and Papyrus bindings the plugin uses (their addresses are SKSE's for 1.7.104).
set(SKSE_SOURCES
    GameAPI.cpp GameBSExtraData.cpp GameData.cpp GameEvents.cpp GameExtraData.cpp GameFormComponents.cpp GameForms.cpp
    GameObjects.cpp GameReferences.cpp GameRTTI.cpp GameThreads.cpp GameTypes.cpp GameUtilities.cpp GameSettings.cpp
    GameResources.cpp GameStreams.cpp GameMenus.cpp GameCamera.cpp GameInput.cpp GameHandlers.cpp GamePathing.cpp
    NiObjects.cpp NiNodes.cpp NiTypes.cpp NiRTTI.cpp NiExtraData.cpp NiGeometry.cpp NiProperties.cpp NiTextures.cpp
    NiMaterial.cpp NiControllers.cpp NiInterpolators.cpp NiLight.cpp NiRenderer.cpp NiSerialization.cpp NiAdditionalGeometryData.cpp
    ObScript.cpp PapyrusArgs.cpp PapyrusInterfaces.cpp PapyrusNativeFunctions.cpp PapyrusValue.cpp PapyrusVM.cpp
    ScaleformAPI.cpp ScaleformCallbacks.cpp ScaleformMovie.cpp ScaleformState.cpp ScaleformTypes.cpp ScaleformValue.cpp
    HashUtil.cpp Translation.cpp BSModelDB.cpp Colors.cpp
)
list(TRANSFORM SKSE_SOURCES PREPEND ${SKSE_ROOT}/skse64/)

add_library(TamrielMergeNative SHARED TamrielMergeNative.cpp ${SKSE_SOURCES})
target_compile_definitions(TamrielMergeNative PRIVATE RUNTIME RUNTIME_VERSION=${RUNTIME_VERSION_PACKED} _CRT_SECURE_NO_WARNINGS NOMINMAX)
# SKSE's source is written for MSVC's permissive mode (as SKSE itself builds).
target_compile_features(TamrielMergeNative PRIVATE cxx_std_17)
target_include_directories(TamrielMergeNative PRIVATE ${SKSE_ROOT})
target_compile_options(TamrielMergeNative PRIVATE /W3 /EHsc /permissive /FI${SKSE_ROOT}/skse64_common/skse_version.h)
target_link_libraries(TamrielMergeNative PRIVATE skse64::skse64_common xse::common)
set_target_properties(TamrielMergeNative skse64_common PROPERTIES MSVC_RUNTIME_LIBRARY "MultiThreaded")
