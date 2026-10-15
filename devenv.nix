{
  pkgs,
  lib,
  ...
}: let
  avaloniaRuntimeLibs = with pkgs; [
    fontconfig
    freetype
    libGL
    libice
    libsm
    libx11
    libxcursor
    libxext
    libxfixes
    libxi
    libxinerama
    libxrandr
    libxrender
    libxcb
    libxkbcommon
    wayland
  ];
in {
  packages = [pkgs.dotnet-sdk_10] ++ avaloniaRuntimeLibs;
  env.LD_LIBRARY_PATH = lib.makeLibraryPath avaloniaRuntimeLibs;
}
