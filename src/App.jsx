import React, { useCallback, useEffect, useRef, useState } from 'react'
import {
  Button,
  CalendarPicker,
  Card,
  Dialog,
  Dropdown,
  Empty,
  Form,
  ImageUploader,
  Input,
  NavBar,
  Popup,
  SearchBar,
  Selector,
  TabBar,
  Tag,
  Toast,
} from 'antd-mobile'
import {
  CloseCircleOutline,
  DeleteOutline,
  EnvironmentOutline,
  LeftOutline,
  ScanCodeOutline,
} from 'antd-mobile-icons'
import SignaturePad from 'signature_pad'

const deliveryStatusOptions = [
  { label: '未配送', value: 'undelivered', color: 'warning' },
  { label: '已配送', value: 'completed', color: 'success' },
]
const deliveryStatusFilterOptions = [
  { label: '全部', value: 'all' },
  ...deliveryStatusOptions.map(item => ({ label: item.label, value: item.value })),
]
const allRouteOption = { label: '全部', value: '全部' }

const fmt = value => {
  const n = Number(value || 0)
  if (Number.isInteger(n)) return `${n}`
  return n.toFixed(2).replace(/0+$/, '').replace(/\.$/, '')
}

const today = () => {
  const d = new Date()
  return `${d.getFullYear()}-${`${d.getMonth() + 1}`.padStart(2, '0')}-${`${d.getDate()}`.padStart(2, '0')}`
}
const formatDate = date => `${date.getFullYear()}-${`${date.getMonth() + 1}`.padStart(2, '0')}-${`${date.getDate()}`.padStart(2, '0')}`
const addDays = (date, days) => {
  const next = new Date(date)
  next.setDate(next.getDate() + days)
  return next
}
const dateRangePresets = [
  { label: '昨天', value: 'yesterday', range: () => [formatDate(addDays(new Date(), -1)), formatDate(addDays(new Date(), -1))] },
  { label: '今天', value: 'today', range: () => [today(), today()] },
  { label: '明天', value: 'tomorrow', range: () => [formatDate(addDays(new Date(), 1)), formatDate(addDays(new Date(), 1))] },
  { label: '近7天', value: 'last7', range: () => [formatDate(addDays(new Date(), -6)), today()] },
]
const statusMeta = status => deliveryStatusOptions.find(item => item.value === status) || deliveryStatusOptions[0]
const maxImageBytes = 500 * 1024
const carLoadLastCarKey = 'wmsDeliveryLastCarLoadCarId'
const getValue = (row, ...keys) => keys.map(key => row?.[key]).find(value => value !== undefined && value !== null) ?? ''
const displayName = operator => operator?.loginName || operator?.login || ''

const apiBase = window.location.protocol === 'file:' ? 'http://127.0.0.1:5189' : ''
const api = async (url, options = {}) => {
  const response = await fetch(`${apiBase}${url}`, {
    ...options,
    headers: options.body instanceof FormData
      ? options.headers
      : { 'Content-Type': 'application/json', ...(options.headers || {}) },
  })
  const contentType = response.headers.get('content-type') || ''
  const data = contentType.includes('application/json') ? await response.json() : null
  if (!response.ok) throw new Error(data?.message || data?.title || '请求失败')
  return data
}
const queryString = params => {
  const search = new URLSearchParams()
  Object.entries(params).forEach(([key, value]) => {
    if (value !== undefined && value !== null && value !== '') search.set(key, value)
  })
  return search.toString()
}
const buildAmapUrl = row => {
  const address = row.address?.trim()
  if (!address) return ''
  const longitude = Number(row.customerLongitude)
  const latitude = Number(row.customerLatitude)
  if (Number.isFinite(longitude) && Number.isFinite(latitude)) {
    return `intent://navi?sourceApplication=wms-delivery&poiname=${encodeURIComponent(address)}&lat=${latitude}&lon=${longitude}&dev=0&style=2#Intent;scheme=androidamap;package=com.autonavi.minimap;end`
  }
  return `intent://keywordNavi?sourceApplication=wms-delivery&keyword=${encodeURIComponent(address)}&style=2#Intent;scheme=androidamap;package=com.autonavi.minimap;end`
}

const loadImage = src => new Promise((resolve, reject) => {
  const img = new Image()
  img.onload = () => resolve(img)
  img.onerror = reject
  img.src = src
})

const canvasToBlob = (canvas, type, quality) => new Promise(resolve => {
  canvas.toBlob(resolve, type, quality)
})

const blobToDataUrl = blob => new Promise(resolve => {
  const reader = new FileReader()
  reader.onload = () => resolve(reader.result)
  reader.readAsDataURL(blob)
})

const compressImageFile = async (file, fallbackName = 'image.jpg') => {
  if (!file || file.size <= maxImageBytes || !file.type?.startsWith('image/')) return file

  const sourceUrl = URL.createObjectURL(file)
  try {
    const img = await loadImage(sourceUrl)
    const canvas = document.createElement('canvas')
    const ctx = canvas.getContext('2d')
    const outputType = 'image/jpeg'
    let scale = Math.min(1, Math.sqrt(maxImageBytes / file.size))
    let quality = 0.82
    let blob = null

    for (let i = 0; i < 24; i += 1) {
      canvas.width = Math.max(1, Math.round((img.naturalWidth || img.width) * scale))
      canvas.height = Math.max(1, Math.round((img.naturalHeight || img.height) * scale))
      ctx.clearRect(0, 0, canvas.width, canvas.height)
      ctx.fillStyle = '#fff'
      ctx.fillRect(0, 0, canvas.width, canvas.height)
      ctx.drawImage(img, 0, 0, canvas.width, canvas.height)
      blob = await canvasToBlob(canvas, outputType, quality)
      if (!blob || blob.size <= maxImageBytes) break
      if (quality > 0.42) {
        quality -= 0.1
      } else {
        scale *= 0.78
      }
    }

    if (!blob) return file
    const sourceName = file.name || fallbackName
    const name = sourceName.replace(/\.[^.]+$/, '') || fallbackName.replace(/\.[^.]+$/, '')
    return new File([blob], `${name}.jpg`, { type: outputType, lastModified: Date.now() })
  } finally {
    URL.revokeObjectURL(sourceUrl)
  }
}

const compressDataUrl = async dataUrl => {
  if (!dataUrl) return ''
  const blob = await (await fetch(dataUrl)).blob()
  const file = new File([blob], 'signature.png', { type: blob.type || 'image/png' })
  const compressed = await compressImageFile(file, 'signature.jpg')
  return blobToDataUrl(compressed)
}

function App() {
  const [operator, setOperator] = useState(() => {
    const saved = localStorage.getItem('wmsDeliveryOperator')
    return saved ? JSON.parse(saved) : null
  })
  const [page, setPage] = useState(operator ? 'list' : 'login')
  const [deliveries, setDeliveries] = useState([])
  const [activeDelivery, setActiveDelivery] = useState(null)
  const [searchText, setSearchText] = useState('')
  const [keyword, setKeyword] = useState('')
  const [routeFilter, setRouteFilter] = useState(['全部'])
  const [routeOptions, setRouteOptions] = useState([allRouteOption])
  const [dateRange, setDateRange] = useState([today(), today()])
  const [datePreset, setDatePreset] = useState(['today'])
  const [statusFilter, setStatusFilter] = useState(['undelivered'])
  const [datePopup, setDatePopup] = useState(false)
  const [scannerVisible, setScannerVisible] = useState(false)
  const [photos, setPhotos] = useState([])
  const [signature, setSignature] = useState('')
  const [loading, setLoading] = useState(false)
  const [todayCompletedCount, setTodayCompletedCount] = useState(0)
  const deliveryRequestRef = useRef(0)
  const [module, setModule] = useState('delivery')
  const [carLoadOptions, setCarLoadOptions] = useState({ cars: [], drivers: [] })
  const [carLoadBillText, setCarLoadBillText] = useState('')
  const [carLoadRows, setCarLoadRows] = useState([])
  const [selectedCarId, setSelectedCarId] = useState('')
  const [selectedDriverId, setSelectedDriverId] = useState('')
  const [selectedHamalIds, setSelectedHamalIds] = useState([])
  const [carLoadSheetVisible, setCarLoadSheetVisible] = useState(false)
  const [manualPickerVisible, setManualPickerVisible] = useState(false)
  const [manualSearchText, setManualSearchText] = useState('')
  const [manualRows, setManualRows] = useState([])
  const [manualSelectedIds, setManualSelectedIds] = useState([])
  const [manualLoading, setManualLoading] = useState(false)
  const [carLoadLoading, setCarLoadLoading] = useState(false)
  const scannerTargetRef = useRef('delivery')

  const loadDeliveries = useCallback(async (overrides = {}) => {
    if (!operator?.loginID) return
    const requestId = deliveryRequestRef.current + 1
    deliveryRequestRef.current = requestId
    const nextRoute = overrides.route ?? routeFilter[0]
    const nextStatus = overrides.status ?? statusFilter[0]
    setLoading(true)
    try {
      const query = queryString({
        loginId: operator.loginID,
        q: keyword,
        route: nextRoute,
        dateFrom: dateRange?.[0],
        dateTo: dateRange?.[1],
        status: nextStatus,
      })
      const nextRows = await api(`/api/deliveries?${query}`)
      if (requestId === deliveryRequestRef.current) {
        setDeliveries(nextRows)
      }
    } catch (err) {
      if (requestId === deliveryRequestRef.current) {
        Toast.show({ icon: 'fail', content: err.message })
      }
    } finally {
      if (requestId === deliveryRequestRef.current) {
        setLoading(false)
      }
    }
  }, [dateRange, keyword, operator?.loginID, routeFilter, statusFilter])

  const loadRoutes = useCallback(async () => {
    if (!operator?.loginID) return
    try {
      const query = queryString({
        loginId: operator.loginID,
        dateFrom: dateRange?.[0],
        dateTo: dateRange?.[1],
        status: statusFilter[0],
      })
      const routes = await api(`/api/routes?${query}`)
      const routeNames = Array.from(new Set((routes || []).filter(Boolean)))
      const nextOptions = [allRouteOption, ...routeNames.map(name => ({ label: name, value: name }))]
      setRouteOptions(nextOptions)
      if (!nextOptions.some(item => item.value === routeFilter[0])) {
        setRouteFilter(['全部'])
      }
    } catch (err) {
      Toast.show({ icon: 'fail', content: err.message })
    }
  }, [dateRange, operator?.loginID, routeFilter, statusFilter])

  const loadDeliverySummary = useCallback(async () => {
    if (!operator?.loginID) return
    try {
      const query = queryString({ loginId: operator.loginID })
      const summary = await api(`/api/delivery-summary?${query}`)
      setTodayCompletedCount(Number(summary?.todayCompletedCount || 0))
    } catch (err) {
      Toast.show({ icon: 'fail', content: err.message })
    }
  }, [operator?.loginID])

  const loadCarLoadOptions = useCallback(async () => {
    if (!operator?.loginID) return
    try {
      const options = await api('/api/carload/options')
      const cars = options?.cars || []
      const drivers = options?.drivers || []
      setCarLoadOptions({ cars, drivers })

      setSelectedCarId(current => {
        if (current && cars.some(car => getValue(car, 'id', 'Id') === current)) return current
        const lastCarId = localStorage.getItem(carLoadLastCarKey) || ''
        if (lastCarId && cars.some(car => getValue(car, 'id', 'Id') === lastCarId)) return lastCarId
        return getValue(cars[0], 'id', 'Id')
      })
      setSelectedDriverId(current => {
        if (current && drivers.some(driver => getValue(driver, 'id', 'Id') === current)) return current
        const currentDriver = drivers.find(driver => getValue(driver, 'id', 'Id') === operator.loginID)
        return getValue(currentDriver || drivers[0], 'id', 'Id')
      })
    } catch (err) {
      Toast.show({ icon: 'fail', content: err.message })
    }
  }, [operator?.loginID])

  const addCarLoadBill = useCallback(async (rawBillCode = carLoadBillText) => {
    const billCode = String(rawBillCode || '').trim()
    if (!billCode) {
      Toast.show('请输入或扫描发货单号')
      return
    }

    setCarLoadLoading(true)
    try {
      const query = queryString({ billCode })
      const row = await api(`/api/carload/scan?${query}`)
      setCarLoadRows(rows => {
        if (rows.some(item => getValue(item, 'id', 'Id') === getValue(row, 'id', 'Id'))) {
          Toast.show('该单据已在本次装车中')
          return rows
        }
        return [row, ...rows]
      })
      setCarLoadBillText('')
      Toast.show({ icon: 'success', content: '已加入本次装车' })
    } catch (err) {
      Toast.show({ icon: 'fail', content: err.message })
    } finally {
      setCarLoadLoading(false)
    }
  }, [carLoadBillText])

  const searchManualBills = useCallback(async (rawKeyword = manualSearchText) => {
    setManualLoading(true)
    try {
      const query = queryString({ q: String(rawKeyword || '').trim() })
      const rows = await api(`/api/carload/pending?${query}`)
      setManualRows(rows || [])
      setManualSelectedIds([])
    } catch (err) {
      Toast.show({ icon: 'fail', content: err.message })
    } finally {
      setManualLoading(false)
    }
  }, [manualSearchText])

  const addManualBills = useCallback(() => {
    const selectedRows = manualRows.filter(row => manualSelectedIds.includes(getValue(row, 'id', 'Id')))
    if (selectedRows.length === 0) {
      Toast.show('请选择待装车单据')
      return
    }
    setCarLoadRows(rows => {
      const existing = new Set(rows.map(row => getValue(row, 'id', 'Id')))
      const nextRows = selectedRows.filter(row => !existing.has(getValue(row, 'id', 'Id')))
      if (nextRows.length === 0) Toast.show('所选单据已在本次装车中')
      return [...nextRows, ...rows]
    })
    setManualPickerVisible(false)
  }, [manualRows, manualSelectedIds])

  const submitCarLoad = useCallback(async () => {
    if (carLoadRows.length === 0) {
      Toast.show('请先加入待装车单据')
      return
    }
    if (!selectedCarId) {
      Toast.show('请选择车辆')
      return
    }
    if (!selectedDriverId) {
      Toast.show('请选择司机')
      return
    }

    setCarLoadLoading(true)
    try {
      const result = await api('/api/carload/submit', {
        method: 'POST',
        body: JSON.stringify({
          loginId: operator.loginID,
          sourceBillIds: carLoadRows.map(row => getValue(row, 'id', 'Id')),
          carId: selectedCarId,
          driverId: selectedDriverId,
          hamalIds: selectedHamalIds,
        }),
      })
      localStorage.setItem(carLoadLastCarKey, selectedCarId)
      setCarLoadRows([])
      setCarLoadBillText('')
      setCarLoadSheetVisible(false)
      Toast.show({ icon: 'success', content: `装车成功：${result?.billCount || 0}单` })
      await loadDeliveries()
    } catch (err) {
      Toast.show({ icon: 'fail', content: err.message })
    } finally {
      setCarLoadLoading(false)
    }
  }, [carLoadRows, loadDeliveries, operator?.loginID, selectedCarId, selectedDriverId, selectedHamalIds])

  const handleScanResult = useCallback(code => {
    const value = String(code || '').trim()
    if (!value) return
    setScannerVisible(false)
    if (scannerTargetRef.current === 'carload' || module === 'carload') {
      setCarLoadBillText(value)
      addCarLoadBill(value)
      return
    }
    setSearchText(value)
    setKeyword(value)
    Toast.show('已识别单号')
  }, [addCarLoadBill, module])

  useEffect(() => {
    const timer = window.setTimeout(loadDeliveries, 180)
    return () => window.clearTimeout(timer)
  }, [loadDeliveries])

  useEffect(() => {
    loadRoutes()
  }, [loadRoutes])

  useEffect(() => {
    loadDeliverySummary()
  }, [loadDeliverySummary])

  useEffect(() => {
    loadCarLoadOptions()
  }, [loadCarLoadOptions])

  useEffect(() => {
    window.__yodexNativeScanResult = handleScanResult
    return () => {
      delete window.__yodexNativeScanResult
    }
  }, [handleScanResult])

  useEffect(() => {
    window.__wmsAndroidBack = () => {
      if (page === 'complete') {
        setPage('detail')
        return true
      }
      if (page === 'detail') {
        setPage('list')
        return true
      }
      if (page === 'list' && module === 'carload') {
        setModule('delivery')
        return true
      }
      return false
    }
    return () => {
      delete window.__wmsAndroidBack
    }
  }, [module, page])

  useEffect(() => {
    window.YodexNative?.setPullRefreshEnabled?.(page === 'list' && module === 'delivery')
  }, [module, page])

  useEffect(() => {
    scannerTargetRef.current = module
  }, [module])

  const openDeliveryDetail = async row => {
    try {
      setLoading(true)
      const detail = await api(`/api/deliveries/${encodeURIComponent(row.id)}`)
      setActiveDelivery(detail)
      setPage('detail')
    } catch (err) {
      Toast.show({ icon: 'fail', content: err.message })
    } finally {
      setLoading(false)
    }
  }

  const completeDelivery = async () => {
    if (!activeDelivery) return
    const formData = new FormData()
    formData.append('loginId', operator.loginID)
    for (const [index, item] of photos.entries()) {
      if (item.file) {
        const file = await compressImageFile(item.file, `delivery-${index + 1}.jpg`)
        formData.append('photos', file, file.name || `delivery-${index + 1}.jpg`)
        continue
      }
      if (item.url) {
        const response = await fetch(item.url)
        const blob = await response.blob()
        const file = await compressImageFile(new File([blob], `delivery-${index + 1}.jpg`, { type: blob.type || 'image/jpeg' }))
        formData.append('photos', file, file.name || `delivery-${index + 1}.jpg`)
      }
    }
    formData.append('signature', await compressDataUrl(signature))
    try {
      await api(`/api/deliveries/${encodeURIComponent(activeDelivery.id)}/complete`, {
        method: 'POST',
        body: formData,
      })
      Toast.show({ icon: 'success', content: '配送完成已保存' })
      setPhotos([])
      setSignature('')
      setActiveDelivery(null)
      setStatusFilter(['undelivered'])
      setPage('list')
      await loadDeliverySummary()
      await loadDeliveries({ status: 'undelivered' })
    } catch (err) {
      Toast.show({ icon: 'fail', content: err.message })
    }
  }

  const logout = async () => {
    const confirmed = await Dialog.confirm({
      title: '确认退出',
      content: '退出后需要重新登录配送账号。',
      confirmText: '退出',
      cancelText: '取消',
    })
    if (!confirmed) return
    localStorage.removeItem('wmsDeliveryOperator')
    setOperator(null)
    setModule('delivery')
    setPage('login')
  }

  const openScanner = (target = module) => {
    scannerTargetRef.current = target
    const nativeBridge = window.YodexNative
    if (nativeBridge && typeof nativeBridge.scanCode === 'function') {
      nativeBridge.scanCode()
      return
    }
    setScannerVisible(true)
  }

  if (!operator) {
    return <LoginPage onLogin={async values => {
      try {
        const user = await api('/api/login', {
          method: 'POST',
          body: JSON.stringify({ login: values.login, password: values.password }),
        })
        localStorage.setItem('wmsDeliveryOperator', JSON.stringify(user))
        setOperator(user)
        setModule('delivery')
        setPage('list')
      } catch (err) {
        Toast.show({ icon: 'fail', content: err.message === '请求失败' ? '司机或密码不正确' : err.message })
      }
    }} />
  }

  return (
    <div className="app">
      {page === 'list' && module === 'delivery' && (
        <DeliveryListPage
          operator={operator}
          module={module}
          rows={deliveries}
          loading={loading}
          todayCompletedCount={todayCompletedCount}
          searchText={searchText}
          routeFilter={routeFilter}
          routeOptions={routeOptions}
          dateRange={dateRange}
          datePreset={datePreset}
          statusFilter={statusFilter}
          onKeyword={setSearchText}
          onSearch={value => setKeyword(String(value || '').trim())}
          onRouteFilter={setRouteFilter}
          onStatusFilter={setStatusFilter}
          onDatePreset={value => {
            setDatePreset(value)
            const preset = dateRangePresets.find(item => item.value === value[0])
            if (preset) setDateRange(preset.range())
          }}
          onModule={setModule}
          onOpenDate={() => setDatePopup(true)}
          onOpenScanner={() => openScanner('delivery')}
          onOpenDetail={openDeliveryDetail}
          logout={logout}
        />
      )}
      {page === 'list' && module === 'carload' && (
        <CarLoadPage
          operator={operator}
          module={module}
          options={carLoadOptions}
          billText={carLoadBillText}
          rows={carLoadRows}
          selectedCarId={selectedCarId}
          selectedDriverId={selectedDriverId}
          selectedHamalIds={selectedHamalIds}
          sheetVisible={carLoadSheetVisible}
          manualVisible={manualPickerVisible}
          manualRows={manualRows}
          manualSearchText={manualSearchText}
          manualSelectedIds={manualSelectedIds}
          manualLoading={manualLoading}
          loading={carLoadLoading}
          onModule={setModule}
          onBillText={setCarLoadBillText}
          onCar={setSelectedCarId}
          onDriver={setSelectedDriverId}
          onHamalIds={setSelectedHamalIds}
          onSheetVisible={setCarLoadSheetVisible}
          onManualVisible={visible => {
            setManualPickerVisible(visible)
            if (visible && manualRows.length === 0) searchManualBills('')
          }}
          onManualSearchText={setManualSearchText}
          onManualSelectedIds={setManualSelectedIds}
          onManualSearch={searchManualBills}
          onManualConfirm={addManualBills}
          onRemoveRow={id => setCarLoadRows(rows => rows.filter(row => getValue(row, 'id', 'Id') !== id))}
          onScan={() => openScanner('carload')}
          onAddBill={addCarLoadBill}
          onSubmit={submitCarLoad}
          logout={logout}
        />
      )}
      {page === 'detail' && activeDelivery && (
        <DeliveryDetailPage
          delivery={activeDelivery}
          onBack={() => setPage('list')}
          onComplete={() => setPage('complete')}
        />
      )}
      {page === 'complete' && activeDelivery && (
        <CompletePage
          delivery={activeDelivery}
          photos={photos}
          signature={signature}
          setPhotos={setPhotos}
          setSignature={setSignature}
          onBack={() => setPage('detail')}
          onSave={completeDelivery}
        />
      )}
      <CalendarPicker
        visible={datePopup}
        selectionMode="range"
        title="选择配送时间段"
        min={new Date(2026, 0, 1)}
        max={new Date(2026, 11, 31)}
        closeOnMaskClick
        onClose={() => setDatePopup(false)}
        onConfirm={value => {
          setDateRange(value?.map(formatDate) || null)
          setDatePreset(['custom'])
          setDatePopup(false)
        }}
      />
      <ScannerPopup
        visible={scannerVisible}
        onClose={() => setScannerVisible(false)}
        onScan={handleScanResult}
      />
    </div>
  )
}

function LoginPage({ onLogin }) {
  return (
    <div className="login-screen">
      <div className="login-hero">
        <h1>WMS配送</h1>
        <p>Yodex移动作业</p>
      </div>
      <Card className="login-card">
        <Form
          layout="vertical"
          onFinish={onLogin}
          footer={<Button block color="primary" size="large" type="submit">登录</Button>}
        >
          <Form.Item name="login" label="司机" rules={[{ required: true, message: '请输入司机' }]}>
            <Input placeholder="司机姓名 / 手机号" clearable />
          </Form.Item>
          <Form.Item name="password" label="密码" rules={[{ required: true, message: '请输入密码' }]}>
            <Input type="password" placeholder="请输入密码" clearable />
          </Form.Item>
        </Form>
      </Card>
    </div>
  )
}

function ModuleSwitch({ active, onChange }) {
  return (
    <TabBar className="module-tabbar" activeKey={active} onChange={onChange}>
      <TabBar.Item key="delivery" title="配送单" icon={<EnvironmentOutline />} />
      <TabBar.Item key="carload" title="扫码装车" icon={<ScanCodeOutline />} />
    </TabBar>
  )
}

function CarLoadPage(props) {
  const {
    operator,
    module,
    options,
    billText,
    rows,
    selectedCarId,
    selectedDriverId,
    selectedHamalIds,
    sheetVisible,
    manualVisible,
    manualRows,
    manualSearchText,
    manualSelectedIds,
    manualLoading,
    loading,
    onModule,
    onBillText,
    onCar,
    onDriver,
    onHamalIds,
    onSheetVisible,
    onManualVisible,
    onManualSearchText,
    onManualSelectedIds,
    onManualSearch,
    onManualConfirm,
    onRemoveRow,
    onScan,
    onAddBill,
    onSubmit,
    logout,
  } = props
  const cars = options?.cars || []
  const drivers = options?.drivers || []
  const carOptions = cars.map(car => ({
    label: getValue(car, 'name', 'Name') || getValue(car, 'code', 'Code') || getValue(car, 'id', 'Id'),
    value: getValue(car, 'id', 'Id'),
  })).filter(item => item.value)
  const driverOptions = drivers.map(driver => ({
    label: getValue(driver, 'name', 'Name') || getValue(driver, 'code', 'Code') || getValue(driver, 'id', 'Id'),
    value: getValue(driver, 'id', 'Id'),
  })).filter(item => item.value)
  const hamalOptions = drivers
    .filter(driver => getValue(driver, 'id', 'Id') !== selectedDriverId)
    .map(driver => ({
      label: getValue(driver, 'name', 'Name') || getValue(driver, 'code', 'Code') || getValue(driver, 'id', 'Id'),
      value: getValue(driver, 'id', 'Id'),
    })).filter(item => item.value)
  const selectedCar = cars.find(car => getValue(car, 'id', 'Id') === selectedCarId)
  const changeCar = value => {
    const nextCarId = value[0] || ''
    onCar(nextCarId)
  }
  const changeDriver = value => {
    const nextDriverId = value[0] || ''
    onDriver(nextDriverId)
    onHamalIds(selectedHamalIds.filter(id => id !== nextDriverId))
  }
  const changeHamals = value => {
    if (value.length > 3) {
      Toast.show('跟车员最多选择3人')
      onHamalIds(value.slice(0, 3))
      return
    }
    onHamalIds(value)
  }
  const totalQty = rows.reduce((sum, row) => sum + Number(getValue(row, 'quantity', 'Quantity') || 0), 0)
  const manualOptions = manualRows.map(row => ({
    value: getValue(row, 'id', 'Id'),
    label: (
      <div className="manual-option">
        <b className="manual-bill-code">{getValue(row, 'billCode', 'BillCode')}</b>
        <b>{getValue(row, 'customerName', 'CustomerName') || '-'}</b>
        <span>{getValue(row, 'address', 'Address') || '-'}</span>
      </div>
    ),
  })).filter(item => item.value)

  return (
    <div className="page-shell carload-shell">
      <NavBar
        className="list-nav"
        backIcon={false}
        left={<div className="nav-user-title">Hi，{displayName(operator)}！发货装车</div>}
        right={<Button fill="none" className="nav-icon-button logout-button" aria-label="退出登录" onClick={logout}><CloseCircleOutline /></Button>}
      />

      <div className="content-list">
        <Card className="carload-panel">
          <div className="carload-scan-row">
            <SearchBar
              value={billText}
              onChange={onBillText}
              onSearch={value => onAddBill(value)}
              onClear={() => onBillText('')}
              placeholder="扫描/输入发货单号"
            />
            <Button fill="outline" color="primary" className="manual-button" onClick={() => onManualVisible(true)}>选单</Button>
            <Button fill="outline" color="primary" className="scan-button" onClick={onScan}><ScanCodeOutline /></Button>
          </div>
        </Card>

        {rows.length === 0 ? (
          <Empty description="扫描或手动选单后显示待装车单据" />
        ) : rows.map(row => (
          <Card className="carload-result-card" key={getValue(row, 'id', 'Id')}>
            <Button className="remove-row-button" size="small" fill="none" onClick={() => onRemoveRow(getValue(row, 'id', 'Id'))}>
              <DeleteOutline /> 移除
            </Button>
            <div className="bill-title-row">
              <div>
                <div className="bill-code">{getValue(row, 'billCode', 'BillCode')}</div>
                <div className="customer-name carload-customer-name">{getValue(row, 'customerName', 'CustomerName') || '-'}</div>
              </div>
            </div>
            <div className="delivery-grid carload-contact-grid">
              <Info label="联系人" value={getValue(row, 'contact', 'Contact') || '-'} />
              <Info label="手机号" value={getValue(row, 'phone', 'Phone') || '-'} href={getValue(row, 'phone', 'Phone') ? `tel:${getValue(row, 'phone', 'Phone')}` : ''} />
              <Info label="线路" value={getValue(row, 'routeName', 'RouteName') || '-'} />
            </div>
            <div className="delivery-grid carload-meta-grid">
              <Info label="地址" value={getValue(row, 'address', 'Address') || '-'} className="wide" />
              {getValue(row, 'comment', 'Comment') && <Info label="备注" value={getValue(row, 'comment', 'Comment')} className="wide" />}
            </div>
          </Card>
        ))}
      </div>

      <div className="bottom-bar carload-bottom-bar">
        <div>合计：<span className="success-text">{rows.length}</span> 单 / <span className="success-text">{fmt(totalQty)}</span></div>
        <Button color="primary" size="large" disabled={rows.length === 0} onClick={() => onSheetVisible(true)}>
          装车
        </Button>
      </div>
      <ModuleSwitch active={module} onChange={onModule} />

      <Popup visible={sheetVisible} position="bottom" bodyClassName="carload-action-popup" closeOnMaskClick onMaskClick={() => onSheetVisible(false)}>
        <div className="carload-popup-body">
          <div className="popup-title">选择装车信息</div>
          <div className="carload-section-label">车辆</div>
          <Selector value={selectedCarId ? [selectedCarId] : []} onChange={changeCar} columns={2} options={carOptions} />
          {selectedCar && <div className="carload-hint">线路：{getValue(selectedCar, 'deliveryAreaNames', 'DeliveryAreaNames') || '-'}</div>}

          <div className="carload-section-label">司机</div>
          <Selector value={selectedDriverId ? [selectedDriverId] : []} onChange={changeDriver} columns={2} options={driverOptions} />

          <div className="carload-section-label">跟车员</div>
          <Selector multiple value={selectedHamalIds} onChange={changeHamals} columns={2} options={hamalOptions} />

          <div className="popup-actions">
            <Button block fill="outline" color="primary" onClick={() => onSheetVisible(false)}>取消</Button>
            <Button block color="primary" loading={loading} disabled={loading} onClick={onSubmit}>确认装车</Button>
          </div>
        </div>
      </Popup>

      <Popup visible={manualVisible} position="right" bodyClassName="manual-picker-popup" destroyOnClose>
        <NavBar backIcon={<LeftOutline />} onBack={() => onManualVisible(false)}>选择待装车单据</NavBar>
        <div className="manual-picker-body">
          <div className="manual-search-row">
            <SearchBar
              value={manualSearchText}
              onChange={onManualSearchText}
              onSearch={onManualSearch}
              placeholder="搜索单号 / 客户 / 线路"
            />
            <Button color="primary" fill="outline" onClick={() => onManualSearch(manualSearchText)}>查询</Button>
          </div>
          {manualRows.length === 0 ? (
            <Empty description={manualLoading ? '正在查询待装车单据' : '暂无待装车单据'} />
          ) : (
            <Selector
              multiple
              value={manualSelectedIds}
              onChange={onManualSelectedIds}
              columns={1}
              options={manualOptions}
            />
          )}
        </div>
        <div className="bottom-bar">
          <div>已选：<span className="success-text">{manualSelectedIds.length}</span> 单</div>
          <Button color="primary" size="large" disabled={manualSelectedIds.length === 0} onClick={onManualConfirm}>带入本次装车</Button>
        </div>
      </Popup>
    </div>
  )
}

function DeliveryListPage(props) {
  const {
    operator,
    module,
    rows,
    loading,
    todayCompletedCount,
    searchText,
    routeFilter,
    routeOptions,
    dateRange,
    datePreset,
    statusFilter,
    onKeyword,
    onSearch,
    onRouteFilter,
    onStatusFilter,
    onDatePreset,
    onModule,
    onOpenDate,
    onOpenScanner,
    onOpenDetail,
    logout,
  } = props
  const dropdownRef = useRef(null)
  const routeTitle = routeFilter[0] === '全部' ? '线路' : routeFilter[0]
  const dateTitle = (() => {
    const preset = dateRangePresets.find(item => item.value === datePreset?.[0])
    if (preset) return preset.label
    return dateRange ? `${dateRange[0]}-${dateRange[1]}` : '时间'
  })()
  const statusTitle = statusFilter[0] === 'all' ? '全部' : statusMeta(statusFilter[0]).label
  const openNavigation = (event, row) => {
    event.stopPropagation()
    const url = buildAmapUrl(row)
    if (!url) {
      Toast.show('客户地址未维护')
      return
    }
    window.location.href = url
  }

  return (
    <div className="page-shell">
      <NavBar
        className="list-nav"
        backIcon={false}
        left={<div className="nav-user-title">Hi，{operator.loginName || operator.login}！今日已配送：{todayCompletedCount}单</div>}
        right={<Button fill="none" className="nav-icon-button logout-button" aria-label="退出登录" onClick={logout}><CloseCircleOutline /></Button>}
      />

      <div className="list-sticky-area">
        <div className="delivery-tools">
          <SearchBar
            value={searchText}
            onChange={onKeyword}
            onSearch={onSearch}
            onClear={() => onSearch('')}
            placeholder="搜索单号 / 客户名称"
          />
          <Button fill="outline" color="primary" className="scan-button" onClick={onOpenScanner}><ScanCodeOutline /></Button>
        </div>
        <Dropdown ref={dropdownRef} className="filter-dropdown" closeOnMaskClick closeOnClickAway>
          <Dropdown.Item key="route" title={routeTitle}>
            <div className="dropdown-panel">
              <Selector
                value={routeFilter}
                onChange={value => {
                  onRouteFilter(value)
                  dropdownRef.current?.close()
                }}
                columns={3}
                options={routeOptions}
              />
            </div>
          </Dropdown.Item>
          <Dropdown.Item key="time" title={dateTitle}>
            <div className="dropdown-panel">
              <Selector
                value={datePreset}
                onChange={value => {
                  onDatePreset(value)
                  dropdownRef.current?.close()
                }}
                columns={4}
                options={dateRangePresets.map(item => ({ label: item.label, value: item.value }))}
              />
              <Button className="custom-date-button" block fill="outline" color="primary" onClick={() => {
                dropdownRef.current?.close()
                onOpenDate()
              }}>
                自定义时间段
              </Button>
            </div>
          </Dropdown.Item>
          <Dropdown.Item key="status" title={statusTitle}>
            <div className="dropdown-panel">
              <Selector
                value={statusFilter}
                onChange={value => {
                  onStatusFilter(value)
                  dropdownRef.current?.close()
                }}
                columns={3}
                options={deliveryStatusFilterOptions}
              />
            </div>
          </Dropdown.Item>
        </Dropdown>
      </div>

      <div className="content-list">
        {rows.length === 0 ? (
          <Empty description={loading ? '正在加载配送单' : '没有待配送单据'} />
        ) : rows.map((row, index) => (
          <Card className="delivery-card" key={row.id} onClick={() => onOpenDetail(row)}>
            <div className="bill-title-row">
              <div className="bill-code-line">
                <span className="item-index-badge">{index + 1}</span>
                <div className="bill-code">{row.billCode}</div>
              </div>
              <Tag color={statusMeta(row.deliveryStatus).color}>{statusMeta(row.deliveryStatus).label}</Tag>
            </div>
            <div className="customer-name">{row.customerName}</div>
            <div className="delivery-grid">
              <Info label="联系人" value={row.contact || '-'} />
              <Info label="线路" value={row.route} />
              <Info label="手机号" value={row.phone || '-'} href={row.phone ? `tel:${row.phone}` : ''} />
              <Info label="距离" value={`${fmt(row.distance)} km`} strong />
            </div>
            {row.comment && <div className="delivery-remark"><span>备注</span><b>{row.comment}</b></div>}
            <div className="address-row">
              <div className="address-line">{row.address || '客户地址未维护'}</div>
              <Button className="navigate-button" fill="outline" color="primary" onClick={event => openNavigation(event, row)}>
                <EnvironmentOutline />
                <span>导航</span>
              </Button>
            </div>
          </Card>
        ))}
      </div>
      <ModuleSwitch active={module} onChange={onModule} />
    </div>
  )
}

function DeliveryDetailPage({ delivery, onBack, onComplete }) {
  const productCount = delivery.products.reduce((sum, row) => sum + Number(row.quantity || 0), 0)
  const isCompleted = delivery.deliveryStatus === 'completed' || Number(delivery.backState || 0) === 1
  return (
    <div className="page-shell">
      <NavBar backIcon={<LeftOutline />} onBack={onBack}>单据详情</NavBar>
      <Card className="detail-head-card">
        <div className="bill-title-row">
          <div>
            <div className="bill-code">{delivery.billCode}</div>
            <div className="bill-sub">{delivery.billDate} · {delivery.carName}</div>
          </div>
          <Tag color={statusMeta(delivery.deliveryStatus).color}>{statusMeta(delivery.deliveryStatus).label}</Tag>
        </div>
        <div className="customer-name">{delivery.customerName}</div>
        <div className="detail-meta-grid">
          <Info label="司机" value={delivery.driverName} />
          <Info label="联系人" value={delivery.contact || '-'} />
          <Info label="手机号" value={delivery.phone || '-'} href={delivery.phone ? `tel:${delivery.phone}` : ''} />
          <Info label="距离" value={`${fmt(delivery.distance)} km`} />
        </div>
        <div className="address-line">{delivery.address || '客户地址未维护'}</div>
      </Card>

      <div className="section-title">商品列表</div>
      <div className="content-list product-list">
        {delivery.products.map((row, index) => (
          <Card className="product-card" key={`${row.id}-${index}`}>
            {(() => {
              const quantityText = row.auxiliaryQuantity || `${fmt(row.quantity)}${row.unit}`
              return (
            <div className="goods-title-row">
              <div className="goods-title-main">
                <div className="goods-name-line">
                  <span className="item-index-badge">{index + 1}</span>
                  <span className="goods-name">{row.name}</span>
                </div>
                <div className="goods-code">{row.barcode}</div>
              </div>
              <div className="qty-badge">{quantityText}</div>
            </div>
              )
            })()}
            <div className="goods-grid compact">
              <Info label="条码" value={row.barcode || '-'} />
              <Info label="规格" value={row.standard || '-'} />
              <Info label="型号" value={row.model || '-'} />
              <Info label="数量" value={`${fmt(row.quantity)} ${row.unit}`} strong />
            </div>
          </Card>
        ))}
      </div>

      <div className="bottom-bar">
        <div>合计：<span className="success-text">{fmt(productCount)}</span></div>
        <Button color="primary" size="large" disabled={isCompleted} onClick={onComplete}>
          {isCompleted ? '已配送' : '配送完成'}
        </Button>
      </div>
    </div>
  )
}

function CompletePage(props) {
  const { delivery, photos, signature, setPhotos, setSignature, onBack, onSave } = props
  const canSave = photos.length > 0 && Boolean(signature)
  const upload = async file => {
    const compressed = await compressImageFile(file)
    if (compressed.size < file.size) {
      Toast.show(`已压缩至 ${Math.ceil(compressed.size / 1024)}KB`)
    }
    return { url: URL.createObjectURL(compressed), file: compressed }
  }

  return (
    <div className="page-shell complete-shell">
      <NavBar backIcon={<LeftOutline />} onBack={onBack}>完成配送</NavBar>
      <Card className="complete-summary">
        <div className="bill-code">{delivery.billCode}</div>
        <div className="bill-sub">{delivery.customerName}</div>
      </Card>

      <Card className="upload-card">
        <div className="card-head">
          <span>拍照上传</span>
          <Tag color="primary" fill="outline">必填</Tag>
        </div>
        <ImageUploader
          value={photos}
          onChange={setPhotos}
          upload={upload}
          maxCount={4}
          accept="image/*"
          capture="environment"
        />
      </Card>

      <Card className="upload-card">
        <div className="card-head">
          <span>客户签字</span>
          <Tag color="primary" fill="outline">必填</Tag>
        </div>
        <SignatureBoxV2 value={signature} onChange={setSignature} />
      </Card>

      <div className="bottom-bar">
        <Button fill="outline" color="primary" size="large" onClick={() => {
          setPhotos([])
          setSignature('')
        }}>
          清空
        </Button>
        <Button color="primary" size="large" disabled={!canSave} onClick={async () => {
          const confirmed = await Dialog.confirm({
            title: '确认完成配送',
            content: '保存照片和客户签字后，单据将标记为已回单。',
            confirmText: '保存',
            cancelText: '取消',
          })
          if (confirmed) onSave()
        }}>
          保存
        </Button>
      </div>
    </div>
  )
}

function SignatureBoxV2({ value, onChange }) {
  const smallCanvasRef = useRef(null)
  const fullCanvasRef = useRef(null)
  const smallPadRef = useRef(null)
  const fullPadRef = useRef(null)
  const valueRef = useRef(value || '')
  const ownChangeRef = useRef('')
  const fullTimerRef = useRef(null)
  const [fullscreen, setFullscreen] = useState(false)
  const [fullReady, setFullReady] = useState(false)

  const createPad = useCallback((canvas, onEnd) => {
    if (!canvas) return null
    const pad = new SignaturePad(canvas, {
      backgroundColor: 'rgb(255,255,255)',
      penColor: '#172033',
      minWidth: 1,
      maxWidth: 2.8,
      throttle: 8,
      minDistance: 1,
    })
    pad.addEventListener('endStroke', onEnd)
    return pad
  }, [])

  const drawDataUrlFit = useCallback((canvas, dataUrl) => {
    if (!canvas || !dataUrl) return
    const ratio = Math.max(window.devicePixelRatio || 1, 1)
    const width = canvas.width / ratio
    const height = canvas.height / ratio
    const img = new Image()
    img.onload = () => {
      const ctx = canvas.getContext('2d')
      const scale = Math.min(width / img.width, height / img.height)
      const drawWidth = img.width * scale
      const drawHeight = img.height * scale
      ctx.drawImage(img, (width - drawWidth) / 2, (height - drawHeight) / 2, drawWidth, drawHeight)
    }
    img.src = dataUrl
  }, [])

  const resizePad = useCallback((canvas, pad, dataUrl) => {
    if (!canvas || !pad) return
    const ratio = Math.max(window.devicePixelRatio || 1, 1)
    const rect = canvas.getBoundingClientRect()
    if (!rect.width || !rect.height) return
    canvas.width = Math.round(rect.width * ratio)
    canvas.height = Math.round(rect.height * ratio)
    canvas.getContext('2d').setTransform(ratio, 0, 0, ratio, 0, 0)
    pad.clear()
    drawDataUrlFit(canvas, dataUrl)
  }, [drawDataUrlFit])

  useEffect(() => {
    valueRef.current = value || ''
    if (value && value === ownChangeRef.current) {
      ownChangeRef.current = ''
      return
    }
    if (!fullscreen) {
      resizePad(smallCanvasRef.current, smallPadRef.current, valueRef.current)
    }
  }, [fullscreen, resizePad, value])

  useEffect(() => {
    const canvas = smallCanvasRef.current
    const handleEnd = () => {
      const nextValue = canvas.toDataURL('image/png')
      valueRef.current = nextValue
      ownChangeRef.current = nextValue
      onChange(nextValue)
    }
    const pad = createPad(canvas, handleEnd)
    smallPadRef.current = pad
    resizePad(canvas, pad, valueRef.current)
    return () => {
      pad?.removeEventListener('endStroke', handleEnd)
      pad?.off()
      smallPadRef.current = null
    }
  }, [createPad, onChange, resizePad])

  const clear = () => {
    smallPadRef.current?.clear()
    fullPadRef.current?.clear()
    valueRef.current = ''
    ownChangeRef.current = ''
    onChange('')
  }

  useEffect(() => {
    if (!fullscreen) {
      setFullReady(false)
      if (fullTimerRef.current) window.clearTimeout(fullTimerRef.current)
      window.YodexNative?.setSignatureFullscreen?.(false)
      try { Promise.resolve(screen.orientation?.unlock?.()).catch(() => {}) } catch {}
      window.setTimeout(() => {
        resizePad(smallCanvasRef.current, smallPadRef.current, valueRef.current)
      }, 250)
      return undefined
    }

    setFullReady(false)
    window.YodexNative?.setSignatureFullscreen?.(true)
    try { Promise.resolve(screen.orientation?.lock?.('landscape')).catch(() => {}) } catch {}

    let retryCount = 0
    let pad = null
    let handleEnd = null
    const initFullPad = () => {
      const canvas = fullCanvasRef.current
      const rect = canvas?.getBoundingClientRect()
      if (!canvas || !rect?.width || !rect?.height) {
        if (retryCount < 12) {
          retryCount += 1
          fullTimerRef.current = window.setTimeout(initFullPad, 120)
        }
        return
      }

      handleEnd = () => {
        const nextValue = canvas.toDataURL('image/png')
        valueRef.current = nextValue
      }
      pad = createPad(canvas, handleEnd)
      fullPadRef.current = pad
      resizePad(canvas, pad, valueRef.current)
      setFullReady(true)
    }
    fullTimerRef.current = window.setTimeout(initFullPad, 500)

    return () => {
      if (fullTimerRef.current) window.clearTimeout(fullTimerRef.current)
      if (handleEnd) pad?.removeEventListener('endStroke', handleEnd)
      pad?.off()
      fullPadRef.current = null
      setFullReady(false)
      window.YodexNative?.setSignatureFullscreen?.(false)
      try { Promise.resolve(screen.orientation?.unlock?.()).catch(() => {}) } catch {}
    }
  }, [createPad, fullscreen, resizePad])

  const saveFullscreen = () => {
    const canvas = fullCanvasRef.current
    if (canvas) {
      const nextValue = canvas.toDataURL('image/png')
      valueRef.current = nextValue
      ownChangeRef.current = nextValue
      onChange(nextValue)
    }
    setFullscreen(false)
  }

  return (
    <div>
      <canvas
        ref={smallCanvasRef}
        className={`signature-canvas${value ? ' has-signature' : ''}`}
      />
      <div className="signature-actions signature-actions-v2">
        <span>请在上方签字</span>
        <Button size="small" fill="none" onClick={() => setFullscreen(true)}>全屏</Button>
        <Button size="small" fill="none" onClick={clear}><DeleteOutline /> 清除</Button>
      </div>
      <Popup visible={fullscreen} position="bottom" bodyClassName="signature-fullscreen" destroyOnClose>
        <div className="signature-fullscreen-body">
          <canvas
            ref={fullCanvasRef}
            className={`signature-canvas signature-canvas-full${value ? ' has-signature' : ''}${fullReady ? ' is-ready' : ''}`}
          />
          <div className="signature-fullscreen-actions">
            <Button fill="outline" color="primary" onClick={clear}>清除</Button>
            <Button fill="outline" color="primary" onClick={() => setFullscreen(false)}>取消</Button>
            <Button color="primary" onClick={saveFullscreen}>完成</Button>
          </div>
        </div>
      </Popup>
    </div>
  )
}

function ScannerPopup({ visible, onClose, onScan }) {
  const videoRef = useRef(null)
  const canvasRef = useRef(null)
  const streamRef = useRef(null)
  const readerRef = useRef(null)
  const animationRef = useRef(0)
  const timeoutRef = useRef(0)
  const scannedRef = useRef(false)
  const fileInputRef = useRef(null)
  const [scannerStatus, setScannerStatus] = useState('正在打开摄像头')
  const [scannerError, setScannerError] = useState('')

  const createScannerReader = useCallback(async () => {
    const [{ BrowserMultiFormatReader }, { BarcodeFormat, DecodeHintType }] = await Promise.all([
      import('@zxing/browser'),
      import('@zxing/library'),
    ])
    const scannerFormats = [
      BarcodeFormat.QR_CODE,
      BarcodeFormat.CODE_128,
      BarcodeFormat.CODE_39,
      BarcodeFormat.CODE_93,
      BarcodeFormat.EAN_13,
      BarcodeFormat.EAN_8,
      BarcodeFormat.UPC_A,
      BarcodeFormat.UPC_E,
      BarcodeFormat.ITF,
      BarcodeFormat.CODABAR,
    ]
    const hints = new Map()
    hints.set(DecodeHintType.POSSIBLE_FORMATS, scannerFormats)
    hints.set(DecodeHintType.TRY_HARDER, true)
    return new BrowserMultiFormatReader(hints)
  }, [])

  const stopScanner = useCallback(() => {
    window.cancelAnimationFrame(animationRef.current)
    window.clearTimeout(timeoutRef.current)
    animationRef.current = 0
    timeoutRef.current = 0
    scannedRef.current = false
    if (streamRef.current) {
      streamRef.current.getTracks().forEach(track => track.stop())
      streamRef.current = null
    }
    if (videoRef.current) {
      videoRef.current.srcObject = null
    }
    readerRef.current = null
  }, [])

  const closeScanner = () => {
    stopScanner()
    onClose()
  }

  const decodeImageFile = async event => {
    const file = event.target.files?.[0]
    event.target.value = ''
    if (!file) return
    const objectUrl = URL.createObjectURL(file)
    try {
      setScannerStatus('正在识别照片')
      const reader = readerRef.current || await createScannerReader()
      const result = await reader.decodeFromImageUrl(objectUrl)
      const text = result?.getText?.() || result?.text || ''
      if (!text.trim()) throw new Error('未识别到条码')
      stopScanner()
      onScan(text.trim())
    } catch (err) {
      setScannerError(err?.message || '未识别到条码，请重新拍照')
    } finally {
      URL.revokeObjectURL(objectUrl)
    }
  }

  useEffect(() => {
    if (!visible) {
      stopScanner()
      return undefined
    }

    let cancelled = false
    scannedRef.current = false
    setScannerStatus('正在打开摄像头')
    setScannerError('')

    const scheduleNextScan = () => {
      timeoutRef.current = window.setTimeout(() => {
        animationRef.current = window.requestAnimationFrame(scanFrame)
      }, 120)
    }

    const scanFrame = () => {
      if (cancelled || scannedRef.current) return
      const video = videoRef.current
      const canvas = canvasRef.current
      const reader = readerRef.current
      if (!video || !canvas || !reader || video.readyState < 2 || !video.videoWidth || !video.videoHeight) {
        scheduleNextScan()
        return
      }

      const videoWidth = video.videoWidth
      const videoHeight = video.videoHeight
      const cropWidth = Math.round(videoWidth * 0.82)
      const cropHeight = Math.round(videoHeight * 0.42)
      const sourceX = Math.round((videoWidth - cropWidth) / 2)
      const sourceY = Math.round((videoHeight - cropHeight) / 2)
      const targetWidth = Math.min(960, cropWidth)
      const targetHeight = Math.round(cropHeight * (targetWidth / cropWidth))
      const context = canvas.getContext('2d', { willReadFrequently: true })

      canvas.width = targetWidth
      canvas.height = targetHeight
      context.drawImage(video, sourceX, sourceY, cropWidth, cropHeight, 0, 0, targetWidth, targetHeight)

      try {
        const result = reader.decodeFromCanvas(canvas)
        const text = result?.getText?.() || result?.text || ''
        if (text.trim()) {
          scannedRef.current = true
          setScannerStatus('识别成功')
          stopScanner()
          onScan(text.trim())
          return
        }
      } catch (err) {
        if (err?.name && err.name !== 'NotFoundException') {
          setScannerStatus('请将条码放入取景框')
        }
      }
      scheduleNextScan()
    }

    const startScanner = async () => {
      try {
        if (!navigator.mediaDevices?.getUserMedia) {
          throw new Error('当前浏览器不支持摄像头扫码')
        }
        readerRef.current = await createScannerReader()
        const stream = await navigator.mediaDevices.getUserMedia({
          video: {
            facingMode: { ideal: 'environment' },
            width: { ideal: 1920 },
            height: { ideal: 1080 },
          },
          audio: false,
        })
        if (cancelled) {
          stream.getTracks().forEach(track => track.stop())
          return
        }
        streamRef.current = stream
        const video = videoRef.current
        video.srcObject = stream
        video.setAttribute('playsinline', 'true')
        video.muted = true
        await video.play()
        setScannerStatus('请将二维码或条形码放入取景框')
        scheduleNextScan()
      } catch (err) {
        setScannerStatus('摄像头不可用')
        setScannerError(err?.message || '请检查浏览器摄像头权限')
      }
    }

    startScanner()

    return () => {
      cancelled = true
      stopScanner()
    }
  }, [visible, onScan, stopScanner, createScannerReader])

  return (
    <Popup visible={visible} position="right" bodyClassName="scanner-popup" destroyOnClose>
      <NavBar onBack={closeScanner}>扫码</NavBar>
      <div className="scanner-stage">
        <video ref={videoRef} className="scanner-video" autoPlay muted playsInline />
        <canvas ref={canvasRef} className="scanner-canvas" />
        <input ref={fileInputRef} className="scanner-file-input" type="file" accept="image/*" capture="environment" onChange={decodeImageFile} />
        <div className="scanner-frame" />
        <div className="scanner-tip">
          <div>{scannerError || scannerStatus}</div>
          <div className="scanner-actions">
            <Button color="primary" fill="outline" onClick={() => fileInputRef.current?.click()}>拍照识别</Button>
            {scannerError && <Button color="primary" fill="outline" onClick={closeScanner}>返回列表</Button>}
          </div>
        </div>
      </div>
    </Popup>
  )
}

function Info({ label, value, strong, href, className = '' }) {
  return (
    <div className={`info-item${className ? ` ${className}` : ''}`}>
      <span>{label}</span>
      {href ? (
        <a className={strong ? 'strong' : ''} href={href} onClick={event => event.stopPropagation()}>{value}</a>
      ) : (
        <b className={strong ? 'strong' : ''}>{value}</b>
      )}
    </div>
  )
}

export default App
