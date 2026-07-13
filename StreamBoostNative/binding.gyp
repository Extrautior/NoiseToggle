{
  "targets": [
    {
      "target_name": "streamboost_hook",
      "sources": [
        "src/addon.cpp",
        "vendor/minhook/src/buffer.c",
        "vendor/minhook/src/hook.c",
        "vendor/minhook/src/trampoline.c",
        "vendor/minhook/src/hde/hde64.c"
      ],
      "include_dirs": [
        "vendor/minhook/include",
        "vendor/minhook/src",
        "vendor/minhook/src/hde"
      ],
      "defines": [
        "NAPI_VERSION=8",
        "WIN32_LEAN_AND_MEAN",
        "NOMINMAX"
      ],
      "msvs_settings": {
        "VCCLCompilerTool": {
          "AdditionalOptions": ["/std:c++20", "/EHsc"],
          "WarningLevel": 4
        }
      }
    },
    {
      "target_name": "discord_voice",
      "sources": ["test/discord_voice_fixture.cpp"],
      "defines": [
        "NAPI_VERSION=8",
        "WIN32_LEAN_AND_MEAN",
        "NOMINMAX"
      ],
      "msvs_settings": {
        "VCCLCompilerTool": {
          "AdditionalOptions": ["/std:c++20", "/EHsc"],
          "WarningLevel": 4
        }
      }
    }
  ]
}
