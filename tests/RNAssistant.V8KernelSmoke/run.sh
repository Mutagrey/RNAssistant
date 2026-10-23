#!/bin/sh
set -eu

root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT HUP INT TERM

cat > "$scratch/production.targets" <<EOF
<Project>
  <ItemGroup>
    <Compile Include="$root/tests/RNAssistant.Harness/ProtectedSecretStoreHarnessStub.cs" />
    <Compile Include="$root/tests/RNAssistant.Harness/AssistantControllerBridgeStub.cs" />
    <Compile Include="$root/tests/RNAssistant.V8KernelSmoke/OfficeFinishBridge.cs" />
    <Compile Include="$root/tests/RNAssistant.Harness/Program.AgentKernelTests.cs" />
    <Compile Include="$root/tests/RNAssistant.V8KernelSmoke/AgentKernelFixtureBridge.cs" />
    <Compile Include="$root/tests/RNAssistant.Harness/Program.ContextContinuityTests.cs" />
    <Compile Include="$root/tests/RNAssistant.V8KernelSmoke/ContextContinuityFixtureBridge.cs" />
  </ItemGroup>
</Project>
EOF

dotnet restore "$root/tests/RNAssistant.Harness/RNAssistant.Harness.csproj" -nologo -v:quiet
dotnet build "$root/tests/RNAssistant.Harness/RNAssistant.Harness.csproj" --no-restore -nologo -v:quiet \
  -p:EnableDefaultCompileItems=false -p:OutputType=Library \
  -p:CustomAfterMicrosoftCommonTargets="$scratch/production.targets"
dotnet run --project "$root/tests/RNAssistant.V8KernelSmoke/RNAssistant.V8KernelSmoke.csproj" --no-launch-profile
