# Linux test rig (PHASE-21-04 T01): runs CodeMap's storage / lock / concurrency suites on native
# Linux semantics (flock, rename(2), errno). Source is COPIED, not bind-mounted: advisory locks over
# Docker Desktop's Windows bind mounts are not reliable, and native behaviour is the point.
# Build + run via tests/docker/run-linux-tests.ps1. Context = repo root; see
# linux-tests.Dockerfile.dockerignore (allow-list — secrets such as .nuget-key never enter).
FROM mcr.microsoft.com/dotnet/sdk:10.0

WORKDIR /src
COPY . .

# Windows checkouts may carry CRLF; bash needs LF.
RUN sed -i 's/\r$//' tests/docker/linux-tests.sh \
 && git config --global --add safe.directory '*' \
 && dotnet build CodeMap.sln -c Debug -warnaserror

# The integration suite indexes the testdata fixtures and assumes they're restored (F12, PHASE-21-07):
# CodeMap.sln doesn't include them, and MSBuildWorkspace doesn't restore.
RUN for s in testdata/SampleSolution/SampleSolution.sln testdata/SampleVbSolution/SampleVbSolution.sln \
             testdata/SampleBlazorSolution/SampleBlazorSolution.slnx testdata/SampleFSharpSolution/SampleFSharpSolution.sln; do \
      dotnet restore "$s" -p:NuGetAudit=false || exit 1; \
    done

# Tests must never share state with anything outside the container run.
ENV CODEMAP_HOME=/tmp/codemap-home \
    TMPDIR=/tmp \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

ENTRYPOINT ["bash", "tests/docker/linux-tests.sh"]
CMD ["all"]
