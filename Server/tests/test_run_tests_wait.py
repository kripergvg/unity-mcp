from unittest.mock import AsyncMock, patch

import pytest

from services.tools.run_tests import _wait_for_test_job


@pytest.mark.asyncio
async def test_wait_for_test_job_returns_terminal_result_without_focus_nudge():
    responses = [
        {
            "success": True,
            "data": {
                "job_id": "job-1",
                "status": "running",
                "last_update_unix_ms": 1,
            },
        },
        {
            "success": True,
            "data": {
                "job_id": "job-1",
                "status": "failed",
                "last_update_unix_ms": 2,
                "result": {
                    "mode": "EditMode",
                    "summary": {
                        "total": 1,
                        "passed": 0,
                        "failed": 1,
                        "skipped": 0,
                        "durationSeconds": 0.1,
                        "resultState": "Failed",
                    },
                    "results": [{
                        "name": "Fails",
                        "fullName": "Example.Tests.Fails",
                        "state": "Failed",
                        "durationSeconds": 0.1,
                        "message": "failure",
                        "stackTrace": "stack",
                        "output": "output",
                    }],
                },
            },
        },
    ]

    with patch(
        "services.tools.run_tests._fetch_test_job",
        new=AsyncMock(side_effect=responses),
    ) as fetch:
        result = await _wait_for_test_job(
            unity_instance=None,
            job_id="job-1",
            include_failed_tests=True,
            include_details=False,
            wait_timeout=5,
        )

    assert result.success is True
    assert result.data is not None
    assert result.data.status == "failed"
    assert result.data.result is not None
    assert result.data.result.results[0].stackTrace == "stack"
    assert fetch.await_count == 2
