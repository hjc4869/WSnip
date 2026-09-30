# WSnip
# =====
#   make                   Build the desktop app for this machine with dotnet.
#   make run               Build and run the desktop app.
#   make publish           Publish the NativeAOT desktop app for $(RID) into artifacts/publish.
#   make flatpak           Build the app inside the Flatpak SDK and produce a Flatpak bundle (Linux).
#   make flatpak-install   Build the Flatpak and install it for the current user.
#   make clean             Remove build artifacts.
#
# WSnip builds against a Light Player checkout, whose shared sources and build scripts it uses.
#
# The Flatpak targets publish the app *inside the Flatpak sandbox* with the
# org.freedesktop.Sdk.Extension.dotnet<N> SDK extension (build-only); the NativeAOT publish is
# self-contained, so no .NET runtime is added to the Flatpak. Restore inside the sandbox is
# offline: `make flatpak` first pins every NuGet package into nuget-sources.json with Light
# Player's packaging/flatpak/nuget-sources.py, then stages the WSnip sources next to the Light
# Player sources they share.
#
# Overridable variables (e.g. `make CONFIGURATION=Debug`, `make RID=linux-arm64`):
#   CONFIGURATION        dotnet configuration (default: Release)
#   RID                  .NET runtime identifier for publishing (default: linux-x64)
#   LIGHTPLAYER_ROOT     Light Player checkout (default: ../LightPlayer)
#   DOTNET_SDK_VERSION   dotnet SDK extension major version (default: 10; must match the manifest)
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

# Build-only .NET SDK provided by the Flatpak SDK extension. These versions must
# match runtime-version / sdk-extensions in the manifest.
DOTNET_SDK_VERSION    ?= 10
FREEDESKTOP_VERSION   ?= 26.08
FLATPAK_SDK           := org.freedesktop.Sdk
DOTNET_SDK_EXTENSION  := org.freedesktop.Sdk.Extension.dotnet$(DOTNET_SDK_VERSION)

# The Light Player projects WSnip references, directly or through each other.
LIGHTPLAYER_PROJECTS  := LightStudio.Logging LightStudio.FfmpegShim LightMediaRenderer

# Offline NuGet feed for the in-sandbox restore, regenerated whenever the projects, the central
# package versions or the configured feeds change.
FLATPAK_NUGET_SOURCES := $(FLATPAK_DIR)/nuget-sources.json
FLATPAK_GENERATOR     := $(LIGHTPLAYER_ROOT)/packaging/flatpak/nuget-sources.py
NUGET_INPUTS          := $(shell find src -name '*.csproj') Directory.Build.props Directory.Packages.props nuget.config \
                         $(foreach project,$(LIGHTPLAYER_PROJECTS),$(LIGHTPLAYER_ROOT)/src/$(project)/$(project).csproj) \
                         $(LIGHTPLAYER_ROOT)/Directory.Packages.props

.PHONY: all build run publish flatpak flatpak-deps flatpak-nuget-sources flatpak-install clean

all: build

build:
	dotnet build "$(DESKTOP_PROJECT)" -c "$(CONFIGURATION)" $(DOTNET_PROPERTIES)

run:
	dotnet run --project "$(DESKTOP_PROJECT)" -c "$(CONFIGURATION)" $(DOTNET_PROPERTIES)

publish:
	dotnet publish "$(DESKTOP_PROJECT)" -c "$(CONFIGURATION)" -r "$(RID)" $(DOTNET_PROPERTIES)

# Ensure the Flathub remote, base SDK, and build-only .NET SDK extension are present.
# The Platform runtime is pulled on demand by --install-deps-from.
flatpak-deps:
	@command -v flatpak >/dev/null 2>&1 || { echo "error: flatpak is required"; exit 1; }
	flatpak remote-add --user --if-not-exists flathub $(FLATHUB_REPO_URL)
	@for ref in $(FLATPAK_SDK)//$(FREEDESKTOP_VERSION) $(DOTNET_SDK_EXTENSION)//$(FREEDESKTOP_VERSION); do \
		flatpak info --arch $(FLATPAK_ARCH) "$$ref" >/dev/null 2>&1 || \
		flatpak install --user --arch $(FLATPAK_ARCH) --assumeyes --noninteractive flathub "$$ref"; \
	done

# Pin the offline NuGet sources inside the SDK extension sandbox (this step needs network).
# -p:Flatpak=true captures the FFmpeg 8.1 bindings and -r $(RID) the self-contained runtime and
# NativeAOT packs for the target. flatpak-deps is an order-only prerequisite so the (always-run)
# phony target does not force regeneration.
$(FLATPAK_NUGET_SOURCES): $(NUGET_INPUTS) | flatpak-deps
	@command -v python3 >/dev/null 2>&1 || { echo "error: python3 is required"; exit 1; }
	@test -f "$(FLATPAK_GENERATOR)" || { echo "error: $(FLATPAK_GENERATOR) is missing; set LIGHTPLAYER_ROOT"; exit 1; }
	# --dotnet-args (argparse REMAINDER) must stay last; without build servers no MSBuild node
	# outlives the restore in the SDK sandbox.
	python3 "$(FLATPAK_GENERATOR)" \
		"$@" "$(DESKTOP_PROJECT)" \
		--dotnet $(DOTNET_SDK_VERSION) --freedesktop $(FREEDESKTOP_VERSION) --runtime $(RID) \
		--dotnet-args --disable-build-servers -p:Flatpak=true -p:SelfContained=true $(DOTNET_PROPERTIES)

# Convenience alias to (re)generate the pinned NuGet sources.
flatpak-nuget-sources: $(FLATPAK_NUGET_SOURCES)

# Build the Flatpak. The inputs are the WSnip tree and the Light Player projects it builds
# against (both staged without bin/ or obj/), the packaging files and the pinned NuGet feed.
flatpak: flatpak-deps $(FLATPAK_NUGET_SOURCES)
	@command -v flatpak-builder >/dev/null 2>&1 || { echo "error: flatpak-builder is required (install 'flatpak-builder')"; exit 1; }
	rm -rf "$(FLATPAK_STAGING)"
	mkdir -p "$(FLATPAK_STAGING)/wsnip" "$(FLATPAK_STAGING)/lightplayer/src"
	cp Directory.Build.props Directory.Packages.props "$(FLATPAK_STAGING)/wsnip/"
	cp -a src "$(FLATPAK_STAGING)/wsnip/"
	cp "$(LIGHTPLAYER_ROOT)/Directory.Build.props" "$(LIGHTPLAYER_ROOT)/Directory.Build.targets" \
		"$(LIGHTPLAYER_ROOT)/Directory.Packages.props" "$(FLATPAK_STAGING)/lightplayer/"
	cp -a "$(LIGHTPLAYER_ROOT)/build" "$(FLATPAK_STAGING)/lightplayer/"
	for project in $(LIGHTPLAYER_PROJECTS); do cp -a "$(LIGHTPLAYER_ROOT)/src/$$project" "$(FLATPAK_STAGING)/lightplayer/src/"; done
	install -D -m 644 "$(LIGHTPLAYER_ROOT)/src/LightStudio.LightPlayer/Controls/Glide.cs" "$(FLATPAK_STAGING)/lightplayer/src/LightStudio.LightPlayer/Controls/Glide.cs"
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
