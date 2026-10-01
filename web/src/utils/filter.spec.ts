/**
 * 筛选规则形状转换的测试。
 *
 * 这一组用例的存在理由是一次真实缺陷（2026-10-01）：界面把编辑器模型直接
 * `JSON.stringify` 存进库，后端认不出形状 → **静默不过滤**，而界面上毫无异常。
 * 所以这里既测"转换对不对"，也测"契约有没有被改坏"。
 */
import { describe, expect, it } from 'vitest'
import type { FileFilterRule } from '@/api/types'
import {
  countConditions,
  emptyFilterRule,
  parseFilterRule,
  serializeFilterRule,
  splitConditions,
} from '@/utils/filter'

/** 解析回 JSON 文本，便于断言线上形状。 */
function wire(json: string): Record<string, unknown> {
  return JSON.parse(json) as Record<string, unknown>
}

function rule(patch: Partial<FileFilterRule>): FileFilterRule {
  return { ...emptyFilterRule(), ...patch }
}

describe('serializeFilterRule：编辑器模型 → 库里的扁平形状', () => {
  it('字段名与后端 FilterRuleSet 逐字对应，IsEnabled 恒为 true', () => {
    const json = serializeFilterRule(
      rule({
        whitelist: { file: '*.docx', folder: '', path: '' },
        blacklist: { file: '', folder: '~临时', path: '' },
      }),
    )

    expect(json).not.toBeNull()
    expect(wire(json!)).toEqual({
      IsEnabled: true,
      UseRegex: false,
      IncludeFiles: '*.docx',
      IncludeFolders: '',
      IncludePaths: '',
      ExcludeFiles: '',
      ExcludeFolders: '~临时',
      ExcludePaths: '',
    })
  })

  it('字段顺序稳定（库里存的是文本，顺序漂移会产生无意义的 diff）', () => {
    const json = serializeFilterRule(rule({ whitelist: { file: '*.docx', folder: '', path: '' } }))

    expect(json).toBe(
      '{"IsEnabled":true,"UseRegex":false,"IncludeFiles":"*.docx","IncludeFolders":"",' +
        '"IncludePaths":"","ExcludeFiles":"","ExcludeFolders":"","ExcludePaths":""}',
    )
  })

  it('多行条件用 CRLF 连接：后端按 Environment.NewLine 拆行，textarea 给的是 LF', () => {
    const json = serializeFilterRule(rule({ whitelist: { file: '*.txt\n*.jpg', folder: '', path: '' } }))
    const value = wire(json!).IncludeFiles as string

    expect(value).toBe('*.txt\r\n*.jpg')
    expect(value.split('\r\n')).toEqual(['*.txt', '*.jpg'])
  })

  it('逐行 trim、丢掉空行与行尾换行', () => {
    const json = serializeFilterRule(
      rule({ whitelist: { file: '  *.txt  \n\n   \r\n*.jpg\n', folder: '', path: '' } }),
    )

    expect(wire(json!).IncludeFiles).toBe('*.txt\r\n*.jpg')
  })

  it('六个组各自独立，黑名单进 Exclude*、白名单进 Include*', () => {
    const json = serializeFilterRule({
      whitelist: { file: 'a', folder: 'b', path: 'c' },
      blacklist: { file: 'd', folder: 'e', path: 'f' },
      useRegex: true,
    })

    expect(wire(json!)).toEqual({
      IsEnabled: true,
      UseRegex: true,
      IncludeFiles: 'a',
      IncludeFolders: 'b',
      IncludePaths: 'c',
      ExcludeFiles: 'd',
      ExcludeFolders: 'e',
      ExcludePaths: 'f',
    })
  })

  it('完全空规则返回 null（后端归一化成 {}，不留空规则）', () => {
    expect(serializeFilterRule(emptyFilterRule())).toBeNull()
    expect(
      serializeFilterRule(rule({ whitelist: { file: '  \n \r\n ', folder: '', path: '' } })),
    ).toBeNull()
  })

  it('只打开正则开关、没有任何条件时也返回 null', () => {
    expect(serializeFilterRule({ ...emptyFilterRule(), useRegex: true })).toBeNull()
  })

  it('极端输入原样带上，不做转义或截断', () => {
    const nasty = '"><script>alert(1)</script>'
    const long = 'x'.repeat(10240)
    const json = serializeFilterRule(
      rule({ whitelist: { file: `${nasty}\n${long}`, folder: '中文 目录名', path: '' } }),
    )

    expect(wire(json!).IncludeFiles).toBe(`${nasty}\r\n${long}`)
    expect(wire(json!).IncludeFolders).toBe('中文 目录名')
  })
})

describe('parseFilterRule：库里的扁平形状 → 编辑器模型', () => {
  it('往返：序列化再解析，得到等价的编辑器模型', () => {
    const original: FileFilterRule = {
      whitelist: { file: '*.docx\n*.xlsx', folder: '', path: '' },
      blacklist: { file: '~$*', folder: '~临时', path: '*/缓存/*' },
      useRegex: true,
    }

    expect(parseFilterRule(serializeFilterRule(original))).toEqual(original)
  })

  it('CRLF 还原成 textarea 用的 LF', () => {
    const parsed = parseFilterRule(
      '{"IsEnabled":true,"UseRegex":false,"IncludeFiles":"*.txt\\r\\n*.jpg",' +
        '"IncludeFolders":"","IncludePaths":"","ExcludeFiles":"","ExcludeFolders":"","ExcludePaths":""}',
    )

    expect(parsed.whitelist.file).toBe('*.txt\n*.jpg')
  })

  it('null / 空串 / 非法 JSON / 非对象，一律得到空规则且不抛错', () => {
    for (const bad of [null, undefined, '', '   ', '{', 'not json', '[1,2]', '3', '"txt"', 'null']) {
      expect(parseFilterRule(bad as string | null | undefined)).toEqual(emptyFilterRule())
    }
  })

  it('旧的嵌套形状不在兼容范围内：得到空规则（而不是伪装成"没有筛选"以外的任何东西）', () => {
    const legacy =
      '{"whitelist":{"file":"*.docx","folder":"","path":""},' +
      '"blacklist":{"file":"","folder":"","path":""},"useRegex":false}'

    expect(parseFilterRule(legacy)).toEqual(emptyFilterRule())
  })

  it('未知字段与已知字段混用时，只认已知字段', () => {
    const parsed = parseFilterRule('{"IncludeFiles":"*.txt","SomethingElse":42}')

    expect(parsed.whitelist.file).toBe('*.txt')
    expect(parsed.useRegex).toBe(false)
  })

  it('字段类型不对时按"没有该条件"处理，而不是把数字塞进文本框', () => {
    const parsed = parseFilterRule('{"IsEnabled":true,"IncludeFiles":123,"ExcludeFolders":["a"]}')

    expect(parsed.whitelist.file).toBe('')
    expect(parsed.blacklist.folder).toBe('')
  })
})

describe('splitConditions / countConditions（既有行为的回归守卫）', () => {
  it('同时接受 LF 与 CRLF，并丢掉空行', () => {
    expect(splitConditions('a\nb\r\nc\n\n ')).toEqual(['a', 'b', 'c'])
    expect(splitConditions('')).toEqual([])
  })

  it('计数覆盖六个组', () => {
    const counted = countConditions({
      whitelist: { file: 'a\nb', folder: 'c', path: '' },
      blacklist: { file: 'd', folder: '', path: 'e\nf' },
      useRegex: false,
    })

    expect(counted).toBe(6)
  })

  it('空规则的计数为 0（"使用当前目录测试"据此判断是否带规则）', () => {
    expect(countConditions(emptyFilterRule())).toBe(0)
  })
})
