# WSnip
# =====
#   make                   Build the desktop app for this machine with dotnet.
#   make run               Build and run the desktop app.
#   make publish           Publish the NativeAOT desktop app for $(RID) into artifacts/publish.
#   make flatpak           Build the app inside the Flatpak SDK and produce a Flatpak bundle (Linux).
#   make flatpak-install   Build the Flatpak and install it for the current user.
#   make clean             Remove build artifacts.
#
# WSnip builds against Light Player, whose shared sources and build scripts it uses. The dotnet
# targets use a local checkout (LIGHTPLAYER_ROOT); the Flatpak targets use the Light Player release
# tag pinned in the Flatpak manifest, which they download and verify themselves.
#
# The Flatpak targets publish the app *inside the Flatpak sandbox* with the
# org.freedesktop.Sdk.Extension.dotnet<N> SDK extension (build-only); the NativeAOT publish is
# self-contained, so no .NET runtime is added to the Flatpak. Restore inside the sandbox is
# offline: `make flatpak` first pins every NuGet package into nuget-sources.json with the
# packaging/flatpak/nuget-sources.py of that Light Player release, then stages the WSnip sources;
# flatpak-builder fetches the same Light Player release archive next to them.
#
# Overridable variables (e.g. `make CONFIGURATION=Debug`, `make RID=linux-arm64`):
#   CONFIGURATION        dotnet configuration (default: Release)
#   RID                  .NET runtime identifier for publishing (default: linux-x64)
#   LIGHTPLAYER_ROOT     Light Player checkout for build/run/publish (default: ../LightPlayer).
#                        The Flatpak targets ignore it and use the release pinned in the manifest.
#   DOTNET_SDK_VERSION   dotnet SDK extension major version (default: 10; must match the manifest)
#   LLVM_SDK_VERSION     LLVM SDK extension major version (default: 22; must match the manifest)
#   FREEDESKTOP_VERSION  freedesktop runtime/SDK version (default: 26.08; must match the manifest)

APP_ID           := im.hjc.WSnip
DESKTOP_PROJECT  := src/WSnip.Desktop/WSnip.Desktop.csproj
CONFIGURATION    ?= Release
RID              ?= linux-x64
LIGHTPLAYER_ROOT ?= $(abspath ../LightPlayer)

# The projects declare <Platforms>x64;ARM64</Platforms>, so an explicit MSBuild
# platform is required; map it from the .NET runtime identifier.
ifeq ($(RID),linux-arm64)
PLATFORM     := ARM64
FLATPAK_ARCH := aarch64
else
PLATFORM     := x64
FLATPAK_ARCH := x86_64
endif

DOTNET_PROPERTIES := -p:Platform=$(PLATFORM) -p:LightPlayerRoot=$(LIGHTPLAYER_ROOT)/

# Flatpak build layout (all under artifacts/, safe to delete).
FLATPAK_DIR      := artifacts/flatpak-$(FLATPAK_ARCH)
FLATPAK_STAGING  := $(FLATPAK_DIR)/staging
FLATPAK_BUILDDIR := $(FLATPAK_DIR)/build
FLATPAK_STATEDIR := $(FLATPAK_DIR)/.flatpak-builder
FLATPAK_REPO     := $(FLATPAK_DIR)/repo
FLATPAK_BUNDLE   := $(FLATPAK_DIR)/$(APP_ID)-$(FLATPAK_ARCH).flatpak
FLATPAK_MANIFEST := packaging/flatpak/$(APP_ID).yml
FLATHUB_REPO_URL := https://dl.flathub.org/repo/flathub.flatpakrepo

# The Light Player release the Flatpak builds against. The manifest is the single place that pins
# it (archive URL and SHA-256); it is read from there so the NuGet pinning below always runs against
# exactly the tree flatpak-builder will build. The download is shared by all architectures.
LIGHTPLAYER_ARCHIVE_URL    := $(shell awk '$$1 == "url:" && $$2 ~ /github\.com\/hjc4869\/LightPlayer\// { print $$2; exit }' $(FLATPAK_MANIFEST))
LIGHTPLAYER_ARCHIVE_SHA256 := $(shell awk '$$1 == "url:" && $$2 ~ /github\.com\/hjc4869\/LightPlayer\// { found = 1 } found && $$1 == "sha256:" { print $$2; exit }' $(FLATPAK_MANIFEST))
LIGHTPLAYER_RELEASE_DIR    := artifacts/lightplayer
# Tag name plus the start of the hash, so changing either the pinned tag or its archive re-downloads.
LIGHTPLAYER_RELEASE_ID     := $(basename $(basename $(notdir $(LIGHTPLAYER_ARCHIVE_URL))))-$(shell printf %.12s '$(LIGHTPLAYER_ARCHIVE_SHA256)')
FLATPAK_LIGHTPLAYER_ROOT   := $(abspath $(LIGHTPLAYER_RELEASE_DIR)/$(LIGHTPLAYER_RELEASE_ID))
FLATPAK_LIGHTPLAYER_STAMP  := $(FLATPAK_LIGHTPLAYER_ROOT)/.extracted
FLATPAK_DOTNET_PROPERTIES := -p:Platform=$(PLATFORM) -p:LightPlayerRoot=$(FLATPAK_LIGHTPLAYER_ROOT)/

# Build-only .NET SDK provided by the Flatpak SDK extension. These versions must
# match runtime-version / sdk-extensions in the manifest.
DOTNET_SDK_VERSION    ?= 10
LLVM_SDK_VERSION      ?= 22
FREEDESKTOP_VERSION   ?= 26.08
FLATPAK_SDK           := org.freedesktop.Sdk
DOTNET_SDK_EXTENSION  := org.freedesktop.Sdk.Extension.dotnet$(DOTNET_SDK_VERSION)
LLVM_SDK_EXTENSION    := org.freedesktop.Sdk.Extension.llvm$(LLVM_SDK_VERSION)

# Offline NuGet feed for the in-sandbox restore, regenerated whenever the projects, the central
# package versions, the configured feeds or the pinned Light Player release change.
FLATPAK_NUGET_SOURCES := $(FLATPAK_DIR)/nuget-sources.json
FLATPAK_GENERATOR     := $(FLATPAK_LIGHTPLAYER_ROOT)/packaging/flatpak/nuget-sources.py
NUGET_INPUTS          := $(shell find src -name '*.csproj') Directory.Build.props Directory.Build.targets Directory.Packages.props nuget.config \
                         $(FLATPAK_LIGHTPLAYER_STAMP)

.PHONY: all build run publish flatpak flatpak-deps flatpak-nuget-sources flatpak-install clean

all: build

build:
	dotnet build "$(DESKTOP_PROJECT)" -c "$(CONFIGURATION)" $(DOTNET_PROPERTIES)

run:
	dotnet run --project "$(DESKTOP_PROJECT)" -c "$(CONFIGURATION)" $(DOTNET_PROPERTIES)

publish:
	dotnet publish "$(DESKTOP_PROJECT)" -c "$(CONFIGURATION)" -r "$(RID)" $(DOTNET_PROPERTIES)

# Ensure the Flathub remote, base SDK, and build-only .NET and LLVM extensions are present.
# The Platform runtime is pulled on demand by --install-deps-from.
flatpak-deps:
	@command -v flatpak >/dev/null 2>&1 || { echo "error: flatpak is required"; exit 1; }
	flatpak remote-add --user --if-not-exists flathub $(FLATHUB_REPO_URL)
	@for ref in $(FLATPAK_SDK)//$(FREEDESKTOP_VERSION) $(DOTNET_SDK_EXTENSION)//$(FREEDESKTOP_VERSION) $(LLVM_SDK_EXTENSION)//$(FREEDESKTOP_VERSION); do \
		flatpak info --arch $(FLATPAK_ARCH) "$$ref" >/dev/null 2>&1 || \
		flatpak install --user --arch $(FLATPAK_ARCH) --assumeyes --noninteractive flathub "$$ref"; \
	done

# Pin the offline NuGet sources inside the SDK extension sandbox (this step needs network).
# -p:Flatpak=true selects the runtime's FFmpeg API and -r $(RID) the self-contained runtime and
# NativeAOT packs for the target. flatpak-deps is an order-only prerequisite so the (always-run)
# phony target does not force regeneration.
$(FLATPAK_NUGET_SOURCES): $(NUGET_INPUTS) Makefile | flatpak-deps
	@command -v python3 >/dev/null 2>&1 || { echo "error: python3 is required"; exit 1; }
	@test -f "$(FLATPAK_GENERATOR)" || { echo "error: $(FLATPAK_GENERATOR) is missing from the Light Player release"; exit 1; }
	# --dotnet-args (argparse REMAINDER) must stay last; without build servers no MSBuild node
	# outlives the restore in the SDK sandbox.
	python3 "$(FLATPAK_GENERATOR)" \
		"$@" "$(DESKTOP_PROJECT)" \
		--dotnet $(DOTNET_SDK_VERSION) --freedesktop $(FREEDESKTOP_VERSION) --runtime $(RID) \
		--dotnet-args --disable-build-servers -p:Flatpak=true -p:SelfContained=true $(FLATPAK_DOTNET_PROPERTIES)

# Download the pinned Light Player release (verifying its SHA-256) and unpack it. The directory
# name encodes the pin, so nothing here goes stale; the stamp marks a complete extraction.
$(FLATPAK_LIGHTPLAYER_STAMP):
	@test -n "$(LIGHTPLAYER_ARCHIVE_URL)" -a -n "$(LIGHTPLAYER_ARCHIVE_SHA256)" || { echo "error: no Light Player archive url/sha256 found in $(FLATPAK_MANIFEST)"; exit 1; }
	@command -v curl >/dev/null 2>&1 || { echo "error: curl is required"; exit 1; }
	mkdir -p "$(LIGHTPLAYER_RELEASE_DIR)"
	curl --fail --location --silent --show-error --retry 3 -o "$(FLATPAK_LIGHTPLAYER_ROOT).tar.gz.part" "$(LIGHTPLAYER_ARCHIVE_URL)"
	echo "$(LIGHTPLAYER_ARCHIVE_SHA256)  $(FLATPAK_LIGHTPLAYER_ROOT).tar.gz.part" | sha256sum --check --status || { echo "error: SHA-256 mismatch for $(LIGHTPLAYER_ARCHIVE_URL)"; rm -f "$(FLATPAK_LIGHTPLAYER_ROOT).tar.gz.part"; exit 1; }
	rm -rf "$(FLATPAK_LIGHTPLAYER_ROOT)"
	mkdir -p "$(FLATPAK_LIGHTPLAYER_ROOT)"
	tar -xzf "$(FLATPAK_LIGHTPLAYER_ROOT).tar.gz.part" -C "$(FLATPAK_LIGHTPLAYER_ROOT)" --strip-components=1
	rm -f "$(FLATPAK_LIGHTPLAYER_ROOT).tar.gz.part"
	touch "$@"

# Convenience alias to (re)generate the pinned NuGet sources.
flatpak-nuget-sources: $(FLATPAK_NUGET_SOURCES)

# Build the Flatpak. The inputs are the WSnip tree (staged without bin/ or obj/), the packaging
# files and the pinned NuGet feed; flatpak-builder itself fetches the Light Player release.
flatpak: flatpak-deps $(FLATPAK_NUGET_SOURCES)
	@command -v flatpak-builder >/dev/null 2>&1 || { echo "error: flatpak-builder is required (install 'flatpak-builder')"; exit 1; }
	rm -rf "$(FLATPAK_STAGING)"
	mkdir -p "$(FLATPAK_STAGING)/wsnip"
	cp Directory.Build.props Directory.Build.targets Directory.Packages.props "$(FLATPAK_STAGING)/wsnip/"
	cp -a src "$(FLATPAK_STAGING)/wsnip/"
	find "$(FLATPAK_STAGING)" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
	cp "$(FLATPAK_MANIFEST)" "$(FLATPAK_NUGET_SOURCES)" LICENSE THIRDPARTY.txt "$(FLATPAK_STAGING)/"
	cp "packaging/flatpak/$(APP_ID).desktop" "packaging/flatpak/$(APP_ID).metainfo.xml" "$(FLATPAK_STAGING)/"
	cp -r packaging/flatpak/icons "$(FLATPAK_STAGING)/"
	flatpak-builder --force-clean --disable-rofiles-fuse --user \
		--arch $(FLATPAK_ARCH) \
		--install-deps-from=flathub \
		"--state-dir=$(FLATPAK_STATEDIR)" "--repo=$(FLATPAK_REPO)" \
		"$(FLATPAK_BUILDDIR)" "$(FLATPAK_STAGING)/$(APP_ID).yml"
	flatpak build-bundle --arch $(FLATPAK_ARCH) "$(FLATPAK_REPO)" "$(FLATPAK_BUNDLE)" $(APP_ID)
	@echo "Flatpak bundle written to $(FLATPAK_BUNDLE)"

flatpak-install: flatpak
	flatpak install --user --reinstall --assumeyes "$(FLATPAK_BUNDLE)"

clean:
	rm -rf artifacts
