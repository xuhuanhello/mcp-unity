import { jest, describe, it, expect, beforeEach } from '@jest/globals';
import * as z from 'zod';
import { ErrorType, McpUnityError } from '../utils/errors.js';
import { registerCaptureScreenshotTool } from '../tools/captureScreenshotTool.js';

const mockSendRequest = jest.fn<(...args: any[]) => Promise<any>>();
const mockMcpUnity = { sendRequest: mockSendRequest };
const mockLogger = { info: jest.fn(), debug: jest.fn(), warn: jest.fn(), error: jest.fn() };
const mockServerTool = jest.fn();
const mockServer = { tool: mockServerTool };

function getHandler() {
  registerCaptureScreenshotTool(mockServer as any, mockMcpUnity as any, mockLogger as any);
  return mockServerTool.mock.calls[0][3] as (params: any) => Promise<any>;
}

describe('capture_screenshot', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('registers the tool with the MCP server', () => {
    getHandler();
    expect(mockServerTool).toHaveBeenCalledWith(
      'capture_screenshot',
      expect.any(String),
      expect.any(Object),
      expect.any(Function)
    );
  });

  it('forwards params and returns text plus an inline image', async () => {
    const handler = getHandler();
    mockSendRequest.mockResolvedValue({
      success: true,
      message: 'Screenshot saved to .unity-mcp/screenshots/a.png (1920x1080, method: screen_capture)',
      path: '.unity-mcp/screenshots/a.png',
      fullPath: '/project/.unity-mcp/screenshots/a.png',
      width: 1920,
      height: 1080,
      method: 'screen_capture',
      image: { data: 'BASE64', mimeType: 'image/jpeg', width: 1024, height: 576 }
    });

    const result = await handler({ source: 'game_view', maxResolution: 1024 });

    expect(mockSendRequest).toHaveBeenCalledWith({
      method: 'capture_screenshot',
      params: { source: 'game_view', maxResolution: 1024 }
    });
    expect(result.content).toEqual([
      { type: 'text', text: expect.stringContaining('.unity-mcp/screenshots/a.png') },
      { type: 'image', data: 'BASE64', mimeType: 'image/jpeg' }
    ]);
    expect(result.data).toMatchObject({ path: '.unity-mcp/screenshots/a.png', method: 'screen_capture' });
  });

  it('returns only text when Unity sends no image', async () => {
    const handler = getHandler();
    mockSendRequest.mockResolvedValue({
      success: true,
      message: 'Screenshot saved',
      path: '.unity-mcp/screenshots/b.png',
      method: 'camera_composite',
      note: 'Edit mode.'
    });

    const result = await handler({ includeImage: false });

    expect(result.content).toHaveLength(1);
    expect(result.content[0].type).toBe('text');
    expect(result.data.note).toBe('Edit mode.');
  });

  it('forwards a region and returns the crop paths', async () => {
    const handler = getHandler();
    const region = { x: 100, y: 50, width: 800, height: 450 };
    mockSendRequest.mockResolvedValue({
      success: true,
      message: 'Screenshot saved',
      path: '.unity-mcp/screenshots/c.png',
      method: 'screen_capture',
      region,
      regionPath: '.unity-mcp/screenshots/c_region_100_50_800x450.png',
      regionFullPath: '/project/.unity-mcp/screenshots/c_region_100_50_800x450.png',
      image: { data: 'CROP', mimeType: 'image/jpeg', width: 800, height: 450 }
    });

    const result = await handler({ region });

    expect(mockSendRequest).toHaveBeenCalledWith({ method: 'capture_screenshot', params: { region } });
    expect(result.content[1]).toEqual({ type: 'image', data: 'CROP', mimeType: 'image/jpeg' });
    expect(result.data).toMatchObject({
      region,
      regionFullPath: '/project/.unity-mcp/screenshots/c_region_100_50_800x450.png'
    });
  });

  it('rejects an incomplete or empty region in the input schema', () => {
    getHandler();
    const schema = z.object(mockServerTool.mock.calls[0][2] as z.ZodRawShape);

    expect(schema.safeParse({ region: { x: 0, y: 0, width: 10, height: 10 } }).success).toBe(true);
    expect(schema.safeParse({ region: { x: 0, y: 0, width: 0, height: 10 } }).success).toBe(false);
    expect(schema.safeParse({ region: { x: -1, y: 0, width: 10, height: 10 } }).success).toBe(false);
    expect(schema.safeParse({ region: { x: 0, y: 0, width: 10 } }).success).toBe(false);
  });

  it('throws a tool execution error when Unity reports failure', async () => {
    const handler = getHandler();
    mockSendRequest.mockResolvedValue({ success: false, message: "GameObject 'Nope' not found." });

    await expect(handler({ source: 'camera', target: 'Nope' })).rejects.toMatchObject({
      type: ErrorType.TOOL_EXECUTION,
      message: "GameObject 'Nope' not found."
    } as Partial<McpUnityError>);
  });
});
