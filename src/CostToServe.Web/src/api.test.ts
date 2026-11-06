import { describe, expect, it } from "vitest";
import { ApiError, failureText } from "./api";

describe("failureText", () => {
  it("keeps the server's message", () => {
    expect(failureText(new ApiError(409, "A1 is rejected for Southeast."))).toBe("A1 is rejected for Southeast.");
  });

  it("explains a dropped connection, which may still have been recorded", () => {
    expect(failureText(new TypeError("Load failed"))).toBe("Couldn't confirm the result because the connection dropped. Showing the latest state.");
  });
});
