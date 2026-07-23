import { beforeEach, describe, expect, it, vi } from "vitest";

const { getConfigMock, listWebdavDirectoryMock } = vi.hoisted(() => ({
  getConfigMock: vi.fn(),
  listWebdavDirectoryMock: vi.fn(),
}));

vi.mock("~/clients/backend-client.server", () => ({
  backendClient: {
    getConfig: getConfigMock,
    listWebdavDirectory: listWebdavDirectoryMock,
  },
  WebdavDirectoryNotFoundError: class WebdavDirectoryNotFoundError extends Error {},
}));

import { isDeletable, loader } from "./route";
import type { ExplorePageData } from "./route";

function loaderArgs(wildcard: string) {
  return {
    request: new Request(`http://localhost/explore/${wildcard}`),
    params: { "*": wildcard },
  } as unknown as Parameters<typeof loader>[0];
}

describe("isDeletable", () => {
  it("is not deletable above two directories deep", () => {
    expect(isDeletable([], false)).toBe(false);
    expect(isDeletable(["content"], false)).toBe(false);
  });

  it("is deletable two or more directories deep when writable", () => {
    expect(isDeletable(["content", "movie"], false)).toBe(true);
    expect(isDeletable(["content", "show", "season"], false)).toBe(true);
  });

  it("is never deletable while WebDAV is read-only", () => {
    expect(isDeletable(["content", "movie"], true)).toBe(false);
  });
});

describe("explore loader read-only state", () => {
  beforeEach(() => {
    getConfigMock.mockReset();
    listWebdavDirectoryMock.mockReset();
    listWebdavDirectoryMock.mockResolvedValue([]);
  });

  it("requests the enforce-readonly setting and reports it enabled", async () => {
    getConfigMock.mockResolvedValue([
      { configName: "webdav.enforce-readonly", configValue: "true" },
    ]);

    const data = (await loader(loaderArgs("content/movie"))) as ExplorePageData;

    expect(getConfigMock).toHaveBeenCalledWith(["webdav.enforce-readonly"]);
    expect(data.enforceReadonly).toBe(true);
  });

  it("reports read-write when the setting is off", async () => {
    getConfigMock.mockResolvedValue([
      { configName: "webdav.enforce-readonly", configValue: "false" },
    ]);

    const data = (await loader(loaderArgs("content/movie"))) as ExplorePageData;

    expect(data.enforceReadonly).toBe(false);
  });

  it("defaults to read-only when the setting is unset or empty, matching the backend", async () => {
    getConfigMock.mockResolvedValueOnce([]);
    expect(((await loader(loaderArgs("content/movie"))) as ExplorePageData).enforceReadonly).toBe(true);

    getConfigMock.mockResolvedValueOnce([
      { configName: "webdav.enforce-readonly", configValue: "" },
    ]);
    expect(((await loader(loaderArgs("content/movie"))) as ExplorePageData).enforceReadonly).toBe(true);
  });
});
