#!/bin/bash
set -e

echo "=== PrepForge Deploy Agent Setup ==="
echo ""

# Check prerequisites
check_command() {
    if ! command -v "$1" &> /dev/null; then
        echo "ERROR: $1 is not installed. $2"
        exit 1
    fi
    echo "OK: $1 found ($(command -v $1))"
}

check_command dotnet "Install .NET 9 SDK: https://dotnet.microsoft.com/download"
check_command node "Install Node.js: https://nodejs.org/"
check_command eas "Install EAS CLI: npm install -g eas-cli"
check_command xcodebuild "Install Xcode from the App Store"
check_command fastlane "Install Fastlane: brew install fastlane"
check_command pod "Install CocoaPods: brew install cocoapods"

# Capture bin dirs so launchd can find them
EAS_BIN_DIR="$(dirname "$(command -v eas)")"
XCODE_BIN_DIR="$(dirname "$(command -v xcodebuild)")"
FASTLANE_BIN_DIR="$(dirname "$(command -v fastlane)")"
POD_BIN_DIR="$(dirname "$(command -v pod)")"
echo "   eas bin dir:        $EAS_BIN_DIR"
echo "   xcodebuild bin dir: $XCODE_BIN_DIR"
echo "   fastlane bin dir:   $FASTLANE_BIN_DIR"
echo "   pod bin dir:        $POD_BIN_DIR"

# Build ExtraPath (deduplicated)
add_to_path() {
    local dir="$1"
    [[ ":$EXTRA_PATH:" != *":$dir:"* ]] && EXTRA_PATH="${EXTRA_PATH:+$EXTRA_PATH:}$dir" || true
}
EXTRA_PATH=""
add_to_path "$EAS_BIN_DIR"
add_to_path "$XCODE_BIN_DIR"
add_to_path "$FASTLANE_BIN_DIR"
add_to_path "$POD_BIN_DIR"

# Verify .NET version
DOTNET_VERSION=$(dotnet --version)
echo "   .NET version: $DOTNET_VERSION"

# Verify EAS login
echo ""
echo "Checking EAS login..."
if ! eas whoami 2>/dev/null; then
    echo "ERROR: Not logged into EAS. Run: eas login"
    exit 1
fi
echo ""

# Verify Xcode command line tools
echo "Checking Xcode..."
xcode-select -p > /dev/null 2>&1 || {
    echo "ERROR: Xcode command line tools not installed. Run: xcode-select --install"
    exit 1
}
echo "OK: Xcode CLT at $(xcode-select -p)"

# Build the agent
echo ""
echo "Building deploy agent..."
dotnet build -c Release

echo ""
echo "=== Setup Complete ==="
echo ""

echo "Configure appsettings.Production.json with:"
echo "  - ServerUrl: your deploy server URL on Railway"
echo "  - AgentApiKey: the agent API key from deploy server config"
echo "  - ProjectPath: absolute path to react/prep-forge directory"
echo "  - KeychainPassword: the password for the keychain"
echo ""
echo "Run manually:"
echo "  dotnet run"
echo ""

# Offer to install as launchd service
read -p "Install as macOS launch agent (auto-start on login)? [y/N] " -n 1 -r
echo ""
if [[ $REPLY =~ ^[Yy]$ ]]; then
    AGENT_DIR="$(cd "$(dirname "$0")" && pwd)"
    PLIST_PATH="$HOME/Library/LaunchAgents/com.prepforge.deploy-agent.plist"
    DOTNET_PATH="$(command -v dotnet)"

    cat > "$PLIST_PATH" << PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>com.prepforge.deploy-agent</string>
    <key>ProgramArguments</key>
    <array>
        <string>${DOTNET_PATH}</string>
        <string>run</string>
        <string>--project</string>
        <string>${AGENT_DIR}</string>
        <string>-c</string>
        <string>Release</string>
    </array>
    <key>WorkingDirectory</key>
    <string>${AGENT_DIR}</string>
    <key>RunAtLoad</key>
    <true/>
    <key>KeepAlive</key>
    <true/>
    <key>SessionCreate</key>
    <true/>
    <key>StandardOutPath</key>
    <string>/tmp/prepforge-deploy-agent.log</string>
    <key>StandardErrorPath</key>
    <string>/tmp/prepforge-deploy-agent-error.log</string>
    <key>EnvironmentVariables</key>
    <dict>
        <key>PATH</key>
        <string>${EXTRA_PATH}:/usr/local/bin:/opt/homebrew/bin:/usr/bin:/bin</string>
$([ -n "${DeployAgent__ServerUrl:-}" ]   && echo "        <key>DeployAgent__ServerUrl</key><string>${DeployAgent__ServerUrl}</string>")
$([ -n "${DeployAgent__AgentApiKey:-}" ] && echo "        <key>DeployAgent__AgentApiKey</key><string>${DeployAgent__AgentApiKey}</string>")
$([ -n "${DeployAgent__ProjectPath:-}" ] && echo "        <key>DeployAgent__ProjectPath</key><string>${DeployAgent__ProjectPath}</string>")
$([ -n "${DeployAgent__KeychainPassword:-}" ] && echo "        <key>DeployAgent__KeychainPassword</key><string>${DeployAgent__KeychainPassword}</string>")
    </dict>
</dict>
</plist>
PLIST

    launchctl load "$PLIST_PATH"
    echo "Launch agent installed and started."
    echo "  Logs: /tmp/prepforge-deploy-agent.log"
    echo "  Stop: launchctl unload $PLIST_PATH"
    echo "  Start: launchctl load $PLIST_PATH"
fi
