# protondrive: build and install
#
#   make                      build a self-contained release for this machine (as your user)
#   sudo make install         install system-wide into /usr/local (PREFIX=/usr for distribution packages)
#   sudo make uninstall       remove it again
#   make dist                 build the release archive artifacts/protondrive-<version>-<rid>.tar.gz
#   make test                 run the unit tests
#
# Building needs the .NET 10 SDK and the NUGET_GITHUB_USER / NUGET_GITHUB_TOKEN variables (see README).
# The installed program does not need .NET: it is self-contained.
# Build as your user and install with sudo: "sudo make install" only copies what "make" built.

PREFIX        ?= /usr/local
DESTDIR       ?=
CONFIGURATION ?= Release
DOTNET        ?= dotnet

ARCH    := $(shell uname -m)
RID     ?= $(if $(filter x86_64 amd64,$(ARCH)),linux-x64,$(if $(filter aarch64 arm64,$(ARCH)),linux-arm64,unsupported-$(ARCH)))
VERSION := $(shell sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)
NAME    := protondrive-$(VERSION)-$(RID)
STAGE   := artifacts/$(NAME)

.PHONY: all publish install uninstall dist test clean

all: publish

# Self-contained single-file build. The two native libraries (Proton crypto, SQLite) stay next to the
# executable instead of being bundled, so nothing is unpacked into users' home folders at run time.
publish:
	@case "$(RID)" in unsupported-*) echo "Unsupported architecture $(ARCH); set RID=linux-x64 or RID=linux-arm64" >&2; exit 1;; esac
	rm -rf $(STAGE)
	$(DOTNET) publish src/ProtonDrive.Cli -c $(CONFIGURATION) -r $(RID) --self-contained \
		-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=none \
		-o $(STAGE)/protondrive
	install -d $(STAGE)/systemd
	install -m 644 packaging/systemd/*.in $(STAGE)/systemd/
	install -m 755 packaging/install.sh $(STAGE)/
	install -m 644 README.md LICENSE $(STAGE)/
	echo "$(VERSION)" > $(STAGE)/VERSION
	@echo "Built $(STAGE). Install with: sudo make install"

# Deliberately not depending on "publish": under sudo there is usually no .NET SDK or NuGet token.
install:
	@test -x $(STAGE)/protondrive/protondrive || { echo "Nothing built for $(RID) yet: run 'make' first (as your user)." >&2; exit 1; }
	sh $(STAGE)/install.sh --prefix "$(PREFIX)" --destdir "$(DESTDIR)"

uninstall:
	sh packaging/install.sh --uninstall --prefix "$(PREFIX)" --destdir "$(DESTDIR)"

dist: publish
	tar -C artifacts -czf artifacts/$(NAME).tar.gz $(NAME)
	@echo "Created artifacts/$(NAME).tar.gz"

test:
	$(DOTNET) run --project tests/ProtonDrive.Sync.Tests

clean:
	rm -rf artifacts
	$(DOTNET) clean --nologo -v quiet
