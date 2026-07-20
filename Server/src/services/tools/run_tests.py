"""Async Unity Test Runner jobs: start + poll."""
from __future__ import annotations

import asyncio
import logging
from typing import Annotated, Any, Literal

from fastmcp import Context
from mcp.types import ToolAnnotations
from pydantic import BaseModel

from models import MCPResponse
from services.registry import mcp_for_unity_tool
from services.tools import get_unity_instance_from_context
from services.tools.preflight import preflight
import transport.unity_transport as unity_transport
from transport.legacy.unity_connection import async_send_command_with_retry

logger = logging.getLogger(__name__)

class RunTestsSummary(BaseModel):
    total: int
    passed: int
    failed: int
    skipped: int
    durationSeconds: float
    resultState: str


class RunTestsTestResult(BaseModel):
    name: str
    fullName: str
    state: str
    durationSeconds: float
    message: str | None = None
    stackTrace: str | None = None
    output: str | None = None


class RunTestsResult(BaseModel):
    mode: str
    summary: RunTestsSummary
    results: list[RunTestsTestResult] | None = None


class RunTestsStartData(BaseModel):
    job_id: str
    status: str
    mode: str | None = None
    include_details: bool | None = None
    include_failed_tests: bool | None = None


class RunTestsStartResponse(MCPResponse):
    data: RunTestsStartData | None = None


class TestJobFailure(BaseModel):
    full_name: str | None = None
    message: str | None = None
    stack_trace: str | None = None
    output: str | None = None


class TestJobProgress(BaseModel):
    completed: int | None = None
    total: int | None = None
    current_test_full_name: str | None = None
    current_test_started_unix_ms: int | None = None
    last_finished_test_full_name: str | None = None
    last_finished_unix_ms: int | None = None
    stuck_suspected: bool | None = None
    editor_is_focused: bool | None = None
    blocked_reason: str | None = None
    failures_so_far: list[TestJobFailure] | None = None
    failures_capped: bool | None = None


class GetTestJobData(BaseModel):
    job_id: str
    status: str
    mode: str | None = None
    started_unix_ms: int | None = None
    finished_unix_ms: int | None = None
    last_update_unix_ms: int | None = None
    progress: TestJobProgress | None = None
    error: str | None = None
    result: RunTestsResult | None = None


class GetTestJobResponse(MCPResponse):
    data: GetTestJobData | None = None


async def _fetch_test_job(
    unity_instance: str | None,
    job_id: str,
    include_failed_tests: bool,
    include_details: bool,
) -> dict[str, Any]:
    params: dict[str, Any] = {"job_id": job_id}
    if include_failed_tests:
        params["includeFailedTests"] = True
    if include_details:
        params["includeDetails"] = True
    return await unity_transport.send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "get_test_job",
        params,
    )


async def _wait_for_test_job(
    unity_instance: str | None,
    job_id: str,
    include_failed_tests: bool,
    include_details: bool,
    wait_timeout: int,
) -> GetTestJobResponse | MCPResponse:
    deadline = asyncio.get_running_loop().time() + wait_timeout
    poll_interval = 0.25
    previous_update = None

    while True:
        response = await _fetch_test_job(
            unity_instance,
            job_id,
            include_failed_tests,
            include_details,
        )
        if not isinstance(response, dict):
            return MCPResponse(success=False, error=str(response))
        if not response.get("success", True):
            return MCPResponse(**response)

        data = response.get("data", {})
        if data.get("status", "") in ("succeeded", "failed", "cancelled"):
            return GetTestJobResponse(**response)

        remaining = deadline - asyncio.get_running_loop().time()
        if remaining <= 0:
            return GetTestJobResponse(**response)

        last_update = data.get("last_update_unix_ms")
        if previous_update is not None and last_update != previous_update:
            poll_interval = 0.25
        else:
            poll_interval = min(2.0, poll_interval * 1.5)
        previous_update = last_update
        await asyncio.sleep(min(poll_interval, remaining))


@mcp_for_unity_tool(
    group="testing",
    description="Starts a Unity test run. Set wait_timeout to wait server-side for completion, or omit it to return a job_id immediately.",
    annotations=ToolAnnotations(
        title="Run Tests",
        destructiveHint=True,
    ),
)
async def run_tests(
    ctx: Context,
    mode: Annotated[Literal["EditMode", "PlayMode"],
                    "Unity test mode to run"] = "EditMode",
    test_names: Annotated[list[str] | str,
                          "Full names of specific tests to run"] | None = None,
    group_names: Annotated[list[str] | str,
                           "Same as test_names, except it allows for Regex"] | None = None,
    category_names: Annotated[list[str] | str,
                              "NUnit category names to filter by"] | None = None,
    exclude_category_names: Annotated[list[str] | str,
                                     "NUnit category names to exclude"] | None = None,
    assembly_names: Annotated[list[str] | str,
                              "Assembly names to filter tests by"] | None = None,
    include_failed_tests: Annotated[bool,
                                    "Include details for failed/skipped tests only (default: false)"] = False,
    include_details: Annotated[bool,
                               "Include details for all tests (default: false)"] = False,
    init_timeout: Annotated[int | None,
                            "Initialization timeout in milliseconds. PlayMode tests may need longer "
                            "due to domain reload (default: 15000). Recommended: 120000 for PlayMode."] = None,
    wait_timeout: Annotated[int | None,
                            "If set, wait up to this many seconds for completion before returning."] = None,
) -> RunTestsStartResponse | GetTestJobResponse | MCPResponse:
    if init_timeout is not None and init_timeout <= 0:
        return MCPResponse(success=False, error="init_timeout must be a positive integer (milliseconds) or None")
    if wait_timeout is not None and wait_timeout <= 0:
        return MCPResponse(success=False, error="wait_timeout must be a positive integer (seconds) or None")

    unity_instance = await get_unity_instance_from_context(ctx)

    gate = await preflight(ctx, requires_no_tests=True, wait_for_no_compile=True, refresh_if_dirty=True)
    if isinstance(gate, MCPResponse):
        return gate

    def _coerce_string_list(value) -> list[str] | None:
        if value is None:
            return None
        if isinstance(value, str):
            return [value] if value.strip() else None
        if isinstance(value, list):
            result = [str(v).strip() for v in value if v and str(v).strip()]
            return result if result else None
        return None

    params: dict[str, Any] = {"mode": mode}
    if (t := _coerce_string_list(test_names)):
        params["testNames"] = t
    if (g := _coerce_string_list(group_names)):
        params["groupNames"] = g
    if (c := _coerce_string_list(category_names)):
        params["categoryNames"] = c
    if (excluded := _coerce_string_list(exclude_category_names)):
        params["excludeCategoryNames"] = excluded
    if (a := _coerce_string_list(assembly_names)):
        params["assemblyNames"] = a
    if include_failed_tests:
        params["includeFailedTests"] = True
    if include_details:
        params["includeDetails"] = True
    if init_timeout is not None and init_timeout > 0:
        params["initTimeout"] = init_timeout

    response = await unity_transport.send_with_unity_instance(
        async_send_command_with_retry,
        unity_instance,
        "run_tests",
        params,
    )

    if isinstance(response, dict):
        if not response.get("success", True):
            return MCPResponse(**response)
        started = RunTestsStartResponse(**response)
        if wait_timeout is not None and started.data is not None:
            return await _wait_for_test_job(
                unity_instance,
                started.data.job_id,
                include_failed_tests,
                include_details,
                wait_timeout,
            )
        return started
    return MCPResponse(success=False, error=str(response))


@mcp_for_unity_tool(
    group="testing",
    description="Polls an async Unity test job by job_id.",
    annotations=ToolAnnotations(
        title="Get Test Job",
        readOnlyHint=True,
    ),
)
async def get_test_job(
    ctx: Context,
    job_id: Annotated[str, "Job id returned by run_tests"],
    include_failed_tests: Annotated[bool,
                                    "Include details for failed/skipped tests only (default: false)"] = False,
    include_details: Annotated[bool,
                               "Include details for all tests (default: false)"] = False,
    wait_timeout: Annotated[int | None,
                            "If set, wait up to this many seconds for tests to complete before returning. "
                            "Reduces polling frequency and avoids client-side loop detection. "
                            "Recommended: 30-60 seconds. Returns immediately if tests complete sooner."] = None,
) -> GetTestJobResponse | MCPResponse:
    unity_instance = await get_unity_instance_from_context(ctx)

    if wait_timeout and wait_timeout > 0:
        return await _wait_for_test_job(
            unity_instance,
            job_id,
            include_failed_tests,
            include_details,
            wait_timeout,
        )

    response = await _fetch_test_job(
        unity_instance,
        job_id,
        include_failed_tests,
        include_details,
    )
    if not isinstance(response, dict):
        return MCPResponse(success=False, error=str(response))
    if not response.get("success", True):
        return MCPResponse(**response)
    return GetTestJobResponse(**response)
